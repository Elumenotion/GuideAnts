using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class IndexAssistantFileMarkdownShadowHandler : JobHandlerBase<IndexAssistantFileMarkdownShadowJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHybridIndexer _indexer;
    private readonly string _storageRoot;

    public IndexAssistantFileMarkdownShadowHandler(
        ILogger<IndexAssistantFileMarkdownShadowHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHybridIndexer indexer,
        IConfiguration configuration) : base(logger)
    {
        _dbFactory = dbFactory;
        _indexer = indexer;
        _storageRoot = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    public override string JobType => nameof(IndexAssistantFileMarkdownShadowJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(IndexAssistantFileMarkdownShadowJob payload, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var shadow = await context.AssistantFileMarkdownShadows
            .Include(s => s.OriginalFile)
            .ThenInclude(f => f.Assistant)
            .FirstOrDefaultAsync(s => s.OriginalAssistantFileId == payload.AssistantFileId, ct);

        if (shadow == null || 
            shadow.Status != DataModel.Models.MarkdownExtractionStatus.Completed || 
            string.IsNullOrEmpty(shadow.StoragePath))
        {
            Logger.LogInformation("Skipping indexing; shadow not ready for AssistantFile {AssistantFileId}", payload.AssistantFileId);
            return true;
        }

        var assistantId = shadow.OriginalFile.AssistantId;
        var storedPath = shadow.StoragePath;
        if (!StoragePathCompatibility.TryResolveExistingFilePath(storedPath, _storageRoot, out var filePath))
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow file missing at '{storedPath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(ct);
            Logger.LogWarning(
                "Skipping indexing without retry; markdown shadow file is missing for AssistantFile {AssistantFileId}. Path={Path}",
                payload.AssistantFileId,
                storedPath);
            return true;
        }

        if (!string.Equals(storedPath, filePath, StringComparison.Ordinal))
        {
            shadow.StoragePath = filePath;
        }

        try
        {
            // Index with AssistantId as the IndexName
            await _indexer.IndexAssistantFileAsync(
                fileId: payload.AssistantFileId,
                assistantId: assistantId,
                filePath: filePath,
                cancellationToken: ct);

            shadow.IsIndexed = true;
            shadow.ErrorMessage = null;
            await context.SaveChangesAsync(ct);
            return true;
        }
        catch (FileNotFoundException ex)
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow file missing at '{filePath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(ct);
            Logger.LogWarning(
                ex,
                "Skipping indexing without retry; markdown shadow file was missing during read for AssistantFile {AssistantFileId}. Path={Path}",
                payload.AssistantFileId,
                filePath);
            return true;
        }
        catch (DirectoryNotFoundException ex)
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow directory missing for '{filePath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(ct);
            Logger.LogWarning(
                ex,
                "Skipping indexing without retry; markdown shadow directory was missing for AssistantFile {AssistantFileId}. Path={Path}",
                payload.AssistantFileId,
                filePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Hybrid indexing failed for AssistantFile {AssistantFileId}", payload.AssistantFileId);
            return false;
        }
    }
}

public record IndexAssistantFileMarkdownShadowJob(Guid AssistantFileId);



