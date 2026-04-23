using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
// Kernel Memory removed - using HybridIndexer instead
using GuideAntsApi.DataModel;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class IndexContentMarkdownShadowHandler : JobHandlerBase<IndexContentMarkdownShadowJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHybridIndexer _indexer;
    private readonly ILogger<IndexContentMarkdownShadowHandler> _log;
    private readonly string _storageRoot;

    public IndexContentMarkdownShadowHandler(
        ILogger<IndexContentMarkdownShadowHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHybridIndexer indexer,
        IConfiguration configuration) : base(logger)
    {
        _dbFactory = dbFactory;
        _indexer = indexer;
        _log = logger;
        _storageRoot = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    public override string JobType => nameof(IndexContentMarkdownShadowJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(IndexContentMarkdownShadowJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var shadow = await context.ContentFileMarkdownShadows
            .Include(s => s.OriginalVersion)
            .ThenInclude(v => v.ContentFile)
            .FirstOrDefaultAsync(s => s.OriginalContentFileVersionId == payload.ContentFileVersionId, cancellationToken);
        if (shadow == null || shadow.Status != DataModel.Models.MarkdownExtractionStatus.Completed || string.IsNullOrEmpty(shadow.StoragePath))
        {
            _log.LogInformation("Skipping indexing; shadow not ready for ContentFileVersion {Id}", payload.ContentFileVersionId);
            return true;
        }

        var projectId = shadow.OriginalVersion.ContentFile.ProjectId;
        var storedPath = shadow.StoragePath;
        if (!StoragePathCompatibility.TryResolveExistingFilePath(storedPath, _storageRoot, out var filePath))
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow file missing at '{storedPath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(cancellationToken);
            _log.LogWarning(
                "Skipping indexing without retry; markdown shadow file is missing for ContentFileVersion {Id}. Path={Path}",
                payload.ContentFileVersionId,
                storedPath);
            return true;
        }

        if (!string.Equals(storedPath, filePath, StringComparison.Ordinal))
        {
            shadow.StoragePath = filePath;
        }

        try
        {
            await _indexer.IndexContentFileAsync(payload.ContentFileVersionId, projectId, filePath, cancellationToken);
            shadow.IsIndexed = true;
            shadow.ErrorMessage = null;
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (FileNotFoundException ex)
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow file missing at '{filePath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(cancellationToken);
            _log.LogWarning(
                ex,
                "Skipping indexing without retry; markdown shadow file was missing during read for ContentFileVersion {Id}. Path={Path}",
                payload.ContentFileVersionId,
                filePath);
            return true;
        }
        catch (DirectoryNotFoundException ex)
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow directory missing for '{filePath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(cancellationToken);
            _log.LogWarning(
                ex,
                "Skipping indexing without retry; markdown shadow directory was missing for ContentFileVersion {Id}. Path={Path}",
                payload.ContentFileVersionId,
                filePath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hybrid indexing failed for ContentFileVersion {Id}", payload.ContentFileVersionId);
            return false;
        }
    }
}


