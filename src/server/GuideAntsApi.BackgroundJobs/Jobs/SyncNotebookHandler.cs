using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class SyncNotebookHandler : JobHandlerBase<SyncNotebookJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly IJobQueueService _jobQueueService;

    public SyncNotebookHandler(
        ILogger<SyncNotebookHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IConfiguration configuration,
        IJobQueueService jobQueueService) : base(logger)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
        _jobQueueService = jobQueueService;
    }

    public override string JobType => nameof(SyncNotebookJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(SyncNotebookJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var notebook = await context.Notebooks
            .Include(n => n.NotebookFiles)
            .FirstOrDefaultAsync(n => n.Id == payload.NotebookId, cancellationToken);
        if (notebook == null)
        {
            Logger.LogWarning("Notebook {NotebookId} not found for sync", payload.NotebookId);
            return true; // treat as handled
        }

        var storageBase = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        var projectSlug = await context.Projects
            .Where(p => p.Id == notebook.ProjectId)
            .Select(p => p.Slug)
            .FirstOrDefaultAsync(cancellationToken) ?? notebook.ProjectId.ToString();
        var notebookSlug = string.IsNullOrWhiteSpace(notebook.Slug) ? notebook.Id.ToString() : notebook.Slug;
        var rootPath = Path.Combine(storageBase, projectSlug, notebookSlug);
        if (!Directory.Exists(rootPath))
        {
            // Legacy fallback.
            rootPath = Path.Combine(storageBase, notebook.ProjectId.ToString(), "notebooks", notebook.Id.ToString());
        }
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }

        var physicalFiles = NotebookSyncFileEnumerator
            .EnumerateSyncableRelativePaths(rootPath, fileNameFilter: f => !IsTemporaryScriptFile(f))
            .ToDictionary(
                relativePath => relativePath,
                relativePath => Path.Combine(rootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString())),
                StringComparer.OrdinalIgnoreCase);

        var dbFiles = notebook.NotebookFiles.ToDictionary(nf => nf.RelativePath, StringComparer.OrdinalIgnoreCase);

        var newFiles = new List<NotebookFile>();
        var updatedFiles = new List<NotebookFile>();

        foreach (var (relativePath, fullPath) in physicalFiles)
        {
            var fileInfo = new FileInfo(fullPath);
            var fileSize = fileInfo.Length;
            var lastModifiedUtc = fileInfo.LastWriteTimeUtc;
            string fileHash;
            try
            {
                fileHash = ComputeSha256(fullPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Skipping file during sync due to access issue: {FullPath}", LogValueSanitizer.Sanitize(fullPath));
                continue;
            }

            if (dbFiles.TryGetValue(relativePath, out var existing))
            {
                if (existing.FileSize != fileSize || existing.LastModifiedUtc != lastModifiedUtc || existing.FileHash != fileHash)
                {
                    existing.FileSize = fileSize;
                    existing.LastModifiedUtc = lastModifiedUtc;
                    existing.FileHash = fileHash;
                    updatedFiles.Add(existing); // Track updated files for re-indexing
                }
            }
            else
            {
                if (await context.NotebookFiles.AnyAsync(n => n.NotebookId == notebook.Id && n.RelativePath == relativePath, cancellationToken))
                {
                    Logger.LogDebug("Notebook file already exists in DB, skipping add: {RelativePath}", relativePath);
                    continue;
                }

                var nf = new NotebookFile
                {
                    NotebookId = notebook.Id,
                    RelativePath = relativePath,
                    FileSize = fileSize,
                    LastModifiedUtc = lastModifiedUtc,
                    FileHash = fileHash,
                };
                nf.GenerateDocumentId(notebook.Id);
                context.NotebookFiles.Add(nf);
                newFiles.Add(nf);
            }
        }

        foreach (var dbFile in dbFiles.Values)
        {
            if (!physicalFiles.ContainsKey(dbFile.RelativePath))
            {
                // Stale rows under a registered mount are DB-only cleanup; sync never deletes host content.
                if (NotebookSyncFileEnumerator.IsUnderRegisteredMount(dbFile.RelativePath, rootPath))
                {
                    Logger.LogDebug(
                        "Removing stale indexed row under registered mount during sync: {RelativePath}",
                        LogValueSanitizer.Sanitize(dbFile.RelativePath));
                }

                // Match NotebookFileSyncService: never remove rows still referenced by chat attachments
                // or content versions (rename keeps the same NotebookFile id; attachments stay valid).
                var hasContentReferences = await context.ContentFileVersions
                    .AnyAsync(v => v.OriginNotebookFileId == dbFile.Id, cancellationToken);
                var hasMessageAttachments = await context.MessageAttachments
                    .AnyAsync(ma => ma.NotebookFileId == dbFile.Id, cancellationToken);
                if (!hasContentReferences && !hasMessageAttachments)
                {
                    context.NotebookFiles.Remove(dbFile);
                }
                else
                {
                    Logger.LogInformation(
                        "Skipped deleting notebook file {NotebookFileId} during sync (missing on disk but still referenced: contentRefs={ContentRefs}, msgAttachments={MsgAttachments})",
                        dbFile.Id, hasContentReferences, hasMessageAttachments);
                }
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message?.Contains("IX_NotebookFiles_RelativePath_NotebookId") == true)
        {
            Logger.LogWarning("Constraint violation during sync for notebook {NotebookId} - file already exists", payload.NotebookId);
            return true;
        }
        catch (DbUpdateConcurrencyException cx)
        {
            Logger.LogWarning(cx, "Concurrency conflict during sync for notebook {NotebookId}; treating as no-op", payload.NotebookId);
            return true;
        }

        // Enqueue processing for newly created files
        foreach (var newFile in newFiles)
        {
            await EnqueueIndexingJobForFile(context, newFile, cancellationToken);
        }

        // Enqueue re-indexing for updated files
        foreach (var updatedFile in updatedFiles)
        {
            Logger.LogInformation("Re-indexing updated notebook file: {RelativePath}", LogValueSanitizer.Sanitize(updatedFile.RelativePath));
            await EnqueueIndexingJobForFile(context, updatedFile, cancellationToken);
        }

        return true;
    }

    private async Task EnqueueIndexingJobForFile(ApplicationDbContext context, NotebookFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.RelativePath);
        var isDirectIndexable = IsDirectIndexable(extension);
        
        if (isDirectIndexable)
        {
            // Directly indexable text files
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(IndexDirectTextFileJob).Replace("Job", string.Empty),
                payload: new IndexDirectTextFileJob(file.Id, IsContentFile: false),
                ct: cancellationToken);
        }
        else
        {
            await EnsureNotebookMarkdownShadowExistsAsync(context, file.Id, cancellationToken);

            // Files that need markdown extraction
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractNotebookFileMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractNotebookFileMarkdownJob(file.Id),
                ct: cancellationToken);
        }
    }

    private async Task EnsureNotebookMarkdownShadowExistsAsync(ApplicationDbContext context, Guid notebookFileId, CancellationToken cancellationToken)
    {
        var exists = await context.NotebookFileMarkdownShadows
            .AnyAsync(s => s.OriginalNotebookFileId == notebookFileId, cancellationToken);

        if (exists)
        {
            return;
        }

        context.NotebookFileMarkdownShadows.Add(new NotebookFileMarkdownShadow
        {
            OriginalNotebookFileId = notebookFileId,
            ContentHash = string.Empty,
            StoragePath = string.Empty,
            FileSize = 0,
            Status = MarkdownExtractionStatus.Pending
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            Logger.LogInformation("Created missing notebook markdown shadow for NotebookFile {NotebookFileId} during sync", notebookFileId);
        }
        catch (DbUpdateException ex)
        {
            // Another worker may have created the shadow concurrently between the existence check and insert.
            Logger.LogDebug(ex, "Concurrent notebook markdown shadow creation detected for NotebookFile {NotebookFileId}", notebookFileId);
            context.ChangeTracker.Clear();

            var existsAfterConflict = await context.NotebookFileMarkdownShadows
                .AnyAsync(s => s.OriginalNotebookFileId == notebookFileId, cancellationToken);
            if (!existsAfterConflict)
            {
                throw;
            }
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace("\\", "/");

    private static bool IsDirectIndexable(string extension)
    {
        var directIndexableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".json", ".xml", ".puml", ".yaml", ".yml", ".csv", ".sql", ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".h"
        };
        return directIndexableExtensions.Contains(extension);
    }

    /// <summary>
    /// Identifies temporary script files created by /execute endpoint in ScriptExecutionAgent.
    /// Pattern: {32-char hex GUID}_script.{sh|ps1|py}
    /// Example: a1b2c3d4e5f678901234567890123456_script.py
    /// </summary>
    private static bool IsTemporaryScriptFile(string filename)
    {
        var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
        return System.Text.RegularExpressions.Regex.IsMatch(filename, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}


