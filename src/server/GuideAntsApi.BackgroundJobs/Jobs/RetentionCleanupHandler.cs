using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class RetentionCleanupHandler : JobHandlerBase<RetentionCleanupJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Number of conversations to collect per batch for bottom-up child deletion.
    /// Kept small to avoid lock escalation (SQL Server escalates to table locks at ~5000 row locks).
    /// With ~100 messages + turns per conversation, 5 conversations keeps us well under that threshold.
    /// </summary>
    private const int ConversationBatchSize = 5;

    public RetentionCleanupHandler(ILogger<RetentionCleanupHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IConfiguration configuration) : base(logger)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public override string JobType => nameof(RetentionCleanupJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(RetentionCleanupJob payload, CancellationToken cancellationToken)
    {
        // Respect the enabled config — if disabled, drain the job without executing.
        var enabled = _configuration.GetValue<bool>("BackgroundJobs:JobTypes:RetentionCleanup:Enabled");
        if (!enabled)
        {
            Logger.LogInformation("RetentionCleanup is disabled in configuration, skipping job for guide {GuideId}", payload.PublishedGuideId);
            return true;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        // Keep individual statements from running too long.
        db.Database.SetCommandTimeout(60);
        var startedAt = DateTime.UtcNow;

        var guide = await db.PublishedGuides
            .FirstOrDefaultAsync(g => g.Id == payload.PublishedGuideId, cancellationToken);
        if (guide is null || guide.RetentionPeriod is null)
        {
            return true; // nothing to do
        }

        // Cutoff is fixed at job start time. Retention 0 = delete conversations and files at least one hour older than job start (cutoff = startedAt - 1h). Otherwise cutoff = startedAt - N days. Oldest conversations are always deleted first (ordering in DeleteConversationsBottomUpAsync).
        var retentionDays = guide.RetentionPeriod.Value;
        var cutoff = retentionDays == 0 ? startedAt.AddHours(-1) : startedAt.AddDays(-retentionDays);
        var notebookId = guide.NotebookId;

        var notebook = await db.Notebooks
            .FirstOrDefaultAsync(n => n.Id == notebookId, cancellationToken);
        if (notebook == null)
        {
            Logger.LogWarning("Notebook {NotebookId} not found for retention cleanup", notebookId);
            return true;
        }

        var projectSlug = await db.Projects
            .Where(p => p.Id == notebook.ProjectId)
            .Select(p => p.Slug)
            .FirstOrDefaultAsync(cancellationToken) ?? notebook.ProjectId.ToString();
        var notebookSlug = string.IsNullOrWhiteSpace(notebook.Slug) ? notebook.Id.ToString() : notebook.Slug;

        Logger.LogInformation(
            "RetentionCleanup starting for guide {GuideId}, notebook {NotebookId}, retentionDays {RetentionDays}, cutoff {CutoffUtc}",
            guide.Id,
            notebookId,
            retentionDays,
            cutoff);

        // ── Bottom-up conversation deletion ──────────────────────────────
        // Delete children first, then parents. Never delete UsageEvents.
        var totalConversationsDeleted = await DeleteConversationsBottomUpAsync(db, notebookId, cutoff, cancellationToken);

        // ── File cleanup (unchanged logic) ───────────────────────────────
        var oldFilesQuery = db.NotebookFiles
            .Where(f => f.NotebookId == notebookId
                && f.LastModifiedUtc < cutoff
                && !f.RelativePath.ToLower().StartsWith("resources/"));
        var oldFileIdsQuery = oldFilesQuery.Select(f => f.Id);

        // Capture paths for physical deletion after DB commit
        var oldRelativePaths = await oldFilesQuery
            .Select(f => f.RelativePath)
            .ToListAsync(cancellationToken);
        var oldFileCount = oldRelativePaths.Count;

        // Collect shadow paths before deleting shadows/files (DocumentChunks cascade via FK)
        var shadowPathsToDelete = await db.NotebookFileMarkdownShadows
            .Where(s => oldFileIdsQuery.Contains(s.OriginalNotebookFileId))
            .Select(s => s.StoragePath)
            .Where(sp => !string.IsNullOrEmpty(sp))
            .ToListAsync(cancellationToken);

        // Nullify ContentFileVersion references before deleting files (lineage breakage is acceptable)
        await db.ContentFileVersions
            .Where(cfv => cfv.OriginNotebookFileId != null && oldFileIdsQuery.Contains(cfv.OriginNotebookFileId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(cfv => cfv.OriginNotebookFileId, (Guid?)null), cancellationToken);

        await db.NotebookFileMarkdownShadows
            .Where(s => oldFileIdsQuery.Contains(s.OriginalNotebookFileId))
            .ExecuteDeleteAsync(cancellationToken);

        await oldFilesQuery.ExecuteDeleteAsync(cancellationToken);

        // Delete physical files and shadow files from disk AFTER DB commit
        DeletePhysicalFiles(notebook.ProjectId, notebook.Id, projectSlug, notebookSlug, oldRelativePaths, shadowPathsToDelete);

        var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
        Logger.LogInformation(
            "RetentionCleanup completed for guide {GuideId}. Deleted {ConvoCount} conversations and {FileCount} files in {ElapsedMs}ms",
            guide.Id,
            totalConversationsDeleted,
            oldFileCount,
            elapsedMs);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Bottom-up conversation deletion
    //  Deletes all children explicitly before removing the conversation
    //  rows so that no cascade triggers wide table-level locks.
    //  UsageEvents are NEVER touched — their FK columns become orphaned
    //  references to deleted rows, preserving usage data exactly as recorded.
    // ─────────────────────────────────────────────────────────────────────

    private async Task<int> DeleteConversationsBottomUpAsync(
        ApplicationDbContext db,
        Guid notebookId,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var totalDeleted = 0;
        var batchNumber = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchNumber++;

            // Step 0: Collect a batch of conversation IDs to process
            var conversationIds = await db.NotebookConversations
                .Where(c => c.NotebookId == notebookId && c.Created < cutoff)
                .OrderBy(c => c.Created)
                .Select(c => c.Id)
                .Take(ConversationBatchSize)
                .ToListAsync(cancellationToken);

            if (conversationIds.Count == 0)
                break;

            // Collect child IDs needed by multiple steps
            var invocationIds = await db.AgentInvocations
                .Where(ai => conversationIds.Contains(ai.ParentConversationId))
                .Select(ai => ai.Id)
                .ToListAsync(cancellationToken);

            var messageIds = await db.NotebookConversationMessages
                .Where(m => conversationIds.Contains(m.NotebookConversationId))
                .Select(m => m.Id)
                .ToListAsync(cancellationToken);

            // ── Step 1: Delete all independent leaves in parallel ──
            // Each parallel task gets its own DbContext (DbContext is not thread-safe).

            var step1Tasks = new List<Task>();

            if (invocationIds.Count > 0)
            {
                step1Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                    await ctx.AgentInvocationMessages
                        .Where(m => invocationIds.Contains(m.AgentInvocationId))
                        .ExecuteDeleteAsync(cancellationToken), cancellationToken));
            }

            if (messageIds.Count > 0)
            {
                step1Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                    await ctx.MessageEditHistories
                        .Where(h => messageIds.Contains(h.MessageId))
                        .ExecuteDeleteAsync(cancellationToken), cancellationToken));

                step1Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                    await ctx.MessageAttachments
                        .Where(a => messageIds.Contains(a.MessageId))
                        .ExecuteDeleteAsync(cancellationToken), cancellationToken));
            }

            step1Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                await ctx.ConversationLocks
                    .Where(l => conversationIds.Contains(l.ConversationId))
                    .ExecuteDeleteAsync(cancellationToken), cancellationToken));

            step1Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                await ctx.Notebooks
                    .Where(n => n.HomePageConversationId != null && conversationIds.Contains(n.HomePageConversationId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.HomePageConversationId, (Guid?)null), cancellationToken), cancellationToken));

            await Task.WhenAll(step1Tasks);

            // ── Step 2: Delete independent mid-level entities in parallel ──
            // AgentInvocations (after breaking self-ref) and Messages are independent of each other.

            var step2Tasks = new List<Task>();

            if (invocationIds.Count > 0)
            {
                step2Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                {
                    // Break self-referential FK, then delete all invocations for these conversations
                    await ctx.AgentInvocations
                        .Where(ai => ai.ParentInvocationId != null && invocationIds.Contains(ai.ParentInvocationId.Value))
                        .ExecuteUpdateAsync(s => s.SetProperty(ai => ai.ParentInvocationId, (Guid?)null), cancellationToken);

                    await ctx.AgentInvocations
                        .Where(ai => conversationIds.Contains(ai.ParentConversationId))
                        .ExecuteDeleteAsync(cancellationToken);
                }, cancellationToken));
            }

            // Messages must be deleted BEFORE turns (Restrict FK on ConversationTurn composite key)
            step2Tasks.Add(ExecuteWithNewContextAsync(async ctx =>
                await ctx.NotebookConversationMessages
                    .Where(m => conversationIds.Contains(m.NotebookConversationId))
                    .ExecuteDeleteAsync(cancellationToken), cancellationToken));

            await Task.WhenAll(step2Tasks);

            // ── Step 3: Delete turns (depends on messages being gone) ──

            await db.ConversationTurns
                .Where(t => conversationIds.Contains(t.NotebookConversationId))
                .ExecuteDeleteAsync(cancellationToken);

            // ── Step 4: Delete the now-empty conversation rows ──

            await db.NotebookConversations
                .Where(c => conversationIds.Contains(c.Id))
                .ExecuteDeleteAsync(cancellationToken);

            totalDeleted += conversationIds.Count;

            Logger.LogInformation(
                "RetentionCleanup batch {BatchNumber}: deleted {BatchCount} conversations (running total {TotalDeleted}) for notebook {NotebookId}",
                batchNumber,
                conversationIds.Count,
                totalDeleted,
                notebookId);

            // Yield between batches to reduce sustained lock pressure
            await Task.Delay(200, cancellationToken);
        }

        Logger.LogInformation(
            "RetentionCleanup conversation deletion complete for notebook {NotebookId}. Batches: {BatchCount}, total deleted: {TotalDeleted}",
            notebookId,
            batchNumber,
            totalDeleted);

        return totalDeleted;
    }

    /// <summary>
    /// Executes an action with a fresh DbContext from the factory.
    /// Required for parallel operations since DbContext is not thread-safe.
    /// </summary>
    private async Task ExecuteWithNewContextAsync(Func<ApplicationDbContext, Task> action, CancellationToken cancellationToken)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken);
        ctx.Database.SetCommandTimeout(60);
        await action(ctx);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Physical file cleanup (runs after all DB operations)
    // ─────────────────────────────────────────────────────────────────────

    private void DeletePhysicalFiles(Guid projectId, Guid notebookId, string projectSlug, string notebookSlug, List<string> relativePaths, List<string> shadowPaths)
    {
        var storageBase = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        var rootPath = Path.Combine(storageBase, projectSlug, notebookSlug);
        if (!Directory.Exists(rootPath))
        {
            rootPath = Path.Combine(storageBase, projectId.ToString(), "notebooks", notebookId.ToString());
        }

        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(rootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to delete notebook file {Path}", fullPath);
                }
            }
        }

        foreach (var shadowPath in shadowPaths)
        {
            if (File.Exists(shadowPath))
            {
                try
                {
                    File.Delete(shadowPath);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to delete shadow file {Path}", shadowPath);
                }
            }
        }
    }
}
