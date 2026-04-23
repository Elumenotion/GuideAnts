using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
// Kernel Memory removed - using HybridIndexer instead
using GuideAntsApi.DataModel;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class IndexNotebookMarkdownShadowHandler : JobHandlerBase<IndexNotebookMarkdownShadowJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHybridIndexer _indexer;
    private readonly ILogger<IndexNotebookMarkdownShadowHandler> _log;
    private readonly string _storageRoot;

    public IndexNotebookMarkdownShadowHandler(
        ILogger<IndexNotebookMarkdownShadowHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHybridIndexer indexer,
        IConfiguration configuration) : base(logger)
    {
        _dbFactory = dbFactory;
        _indexer = indexer;
        _log = logger;
        _storageRoot = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    public override string JobType => nameof(IndexNotebookMarkdownShadowJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(IndexNotebookMarkdownShadowJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var shadow = await context.NotebookFileMarkdownShadows
            .Include(s => s.OriginalFile)
            .ThenInclude(f => f.Notebook)
            .FirstOrDefaultAsync(s => s.OriginalNotebookFileId == payload.NotebookFileId, cancellationToken);
        if (shadow == null || shadow.Status != DataModel.Models.MarkdownExtractionStatus.Completed || string.IsNullOrEmpty(shadow.StoragePath))
        {
            _log.LogInformation("Skipping indexing; shadow not ready for NotebookFile {Id}", payload.NotebookFileId);
            return true;
        }

        var projectId = shadow.OriginalFile.Notebook.ProjectId;
        var storedPath = shadow.StoragePath;
        if (!StoragePathCompatibility.TryResolveExistingFilePath(storedPath, _storageRoot, out var filePath))
        {
            shadow.IsIndexed = false;
            shadow.ErrorMessage = $"Markdown shadow file missing at '{storedPath}'. Skipping indexing without retry.";
            await context.SaveChangesAsync(cancellationToken);
            _log.LogWarning(
                "Skipping indexing without retry; markdown shadow file is missing for NotebookFile {Id}. Path={Path}",
                payload.NotebookFileId,
                storedPath);
            return true;
        }

        if (!string.Equals(storedPath, filePath, StringComparison.Ordinal))
        {
            shadow.StoragePath = filePath;
        }

        try
        {
            await _indexer.IndexNotebookFileAsync(payload.NotebookFileId, shadow.OriginalFile.NotebookId, projectId, filePath, cancellationToken);
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
                "Skipping indexing without retry; markdown shadow file was missing during read for NotebookFile {Id}. Path={Path}",
                payload.NotebookFileId,
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
                "Skipping indexing without retry; markdown shadow directory was missing for NotebookFile {Id}. Path={Path}",
                payload.NotebookFileId,
                filePath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hybrid indexing failed for NotebookFile {Id}", payload.NotebookFileId);
            return false;
        }
    }
}


