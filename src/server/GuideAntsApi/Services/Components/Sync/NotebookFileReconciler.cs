using System.Text.Json;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Core;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Components.Sync;

public sealed class NotebookFileReconciler : INotebookFileReconciler
{
    private static readonly TimeSpan ReconcileLockTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStoragePathResolver _pathResolver;
    private readonly IJobQueueService _jobQueueService;
    private readonly IFileLineageService _lineageService;
    private readonly IUsageRecorder _usageRecorder;
    private readonly INotebookLockService _lockService;
    private readonly ILogger<NotebookFileReconciler> _logger;

    public NotebookFileReconciler(
        IServiceScopeFactory scopeFactory,
        IStoragePathResolver pathResolver,
        IJobQueueService jobQueueService,
        IFileLineageService lineageService,
        IUsageRecorder usageRecorder,
        INotebookLockService lockService,
        ILogger<NotebookFileReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _pathResolver = pathResolver;
        _jobQueueService = jobQueueService;
        _lineageService = lineageService;
        _usageRecorder = usageRecorder;
        _lockService = lockService;
        _logger = logger;
    }

    public async Task RegisterFilesAsync(
        Guid notebookId,
        IReadOnlyList<string> dbRelativePaths,
        CancellationToken cancellationToken = default)
    {
        if (dbRelativePaths.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebook = await context.Notebooks.FirstOrDefaultAsync(n => n.Id == notebookId, cancellationToken);
        if (notebook == null)
        {
            _logger.LogWarning("Skipping register for missing notebook {NotebookId}", notebookId);
            return;
        }

        var rootPath = _pathResolver.GetNotebookRootPath(notebook.ProjectId, notebookId);
        var changed = false;

        foreach (var rawPath in dbRelativePaths)
        {
            var relativePath = NotebookPathResolver.NormalizeRelativePath(rawPath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var fullPath = Path.Combine(rootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning(
                    "Skipping register for missing notebook file {RelativePath} in notebook {NotebookId}",
                    relativePath,
                    notebookId);
                continue;
            }

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping register due to file access error: {FullPath}", fullPath);
                continue;
            }

            var placeholderHash = NotebookFileHash.Placeholder(fileInfo.Length, fileInfo.LastWriteTimeUtc);
            var existing = await context.NotebookFiles
                .FirstOrDefaultAsync(
                    f => f.NotebookId == notebookId && f.RelativePath == relativePath,
                    cancellationToken);

            if (existing != null)
            {
                if (existing.FileSize != fileInfo.Length ||
                    existing.LastModifiedUtc != fileInfo.LastWriteTimeUtc ||
                    existing.FileHash != placeholderHash)
                {
                    existing.FileSize = fileInfo.Length;
                    existing.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
                    existing.FileHash = placeholderHash;
                    changed = true;
                }
            }
            else
            {
                var nf = new NotebookFile
                {
                    NotebookId = notebookId,
                    RelativePath = relativePath,
                    FileSize = fileInfo.Length,
                    LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                    FileHash = placeholderHash,
                };
                nf.GenerateDocumentId(notebookId);
                context.NotebookFiles.Add(nf);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException dbEx) when (IsDuplicateNotebookFileConstraint(dbEx))
        {
            _logger.LogDebug("Concurrent register insert for notebook {NotebookId}; treating as success", notebookId);
        }
    }

    public async Task<ReconcileResult> ReconcileNotebookAsync(
        Guid notebookId,
        ReconcileMode mode = ReconcileMode.Full,
        CancellationToken cancellationToken = default)
    {
        if (mode != ReconcileMode.Full)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Full reconcile is supported.");
        }

        await using var lockHandle = await _lockService.TryAcquireAsync(notebookId, ReconcileLockTimeout);
        if (!lockHandle.Acquired)
        {
            _logger.LogDebug(
                "Skipping reconcile for notebook {NotebookId} - another sync is already running",
                notebookId);
            return new ReconcileResult();
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebook = await context.Notebooks
            .Include(n => n.NotebookFiles)
            .FirstOrDefaultAsync(n => n.Id == notebookId, cancellationToken);
        if (notebook == null)
        {
            _logger.LogWarning("Notebook {NotebookId} not found for reconcile", notebookId);
            return new ReconcileResult();
        }

        var rootPath = _pathResolver.GetNotebookRootPath(notebook.ProjectId, notebookId);
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }

        var physicalFiles = NotebookSyncFileEnumerator
            .EnumerateSyncableRelativePaths(
                rootPath,
                fileNameFilter: f => !NotebookFileIndexingRules.IsTemporaryScriptFile(f))
            .ToDictionary(
                relativePath => relativePath,
                relativePath => Path.Combine(rootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString())),
                StringComparer.OrdinalIgnoreCase);

        var dbFiles = notebook.NotebookFiles.ToDictionary(nf => nf.RelativePath, StringComparer.OrdinalIgnoreCase);
        var newFiles = new List<NotebookFile>();
        var updatedFiles = new List<NotebookFile>();

        foreach (var (relativePath, fullPath) in physicalFiles)
        {
            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping file during reconcile due to access issue: {FullPath}", fullPath);
                continue;
            }

            string fileHash;
            try
            {
                fileHash = NotebookFileHash.ComputeSha256(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping file during reconcile due to hash failure: {FullPath}", fullPath);
                continue;
            }

            if (dbFiles.TryGetValue(relativePath, out var existing))
            {
                if (existing.FileSize != fileInfo.Length ||
                    existing.LastModifiedUtc != fileInfo.LastWriteTimeUtc ||
                    existing.FileHash != fileHash)
                {
                    existing.FileSize = fileInfo.Length;
                    existing.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
                    existing.FileHash = fileHash;

                    await _lineageService.RecordAsync(
                        FileKind.Notebook,
                        notebook.ProjectId,
                        existing.Id,
                        null,
                        FileLineageAction.ExternalWrite,
                        notebookId,
                        fullPath,
                        saveImmediately: false);

                    updatedFiles.Add(existing);
                }
            }
            else
            {
                if (await context.NotebookFiles.AnyAsync(
                        n => n.NotebookId == notebookId && n.RelativePath == relativePath,
                        cancellationToken))
                {
                    continue;
                }

                var nf = new NotebookFile
                {
                    NotebookId = notebookId,
                    RelativePath = relativePath,
                    FileSize = fileInfo.Length,
                    LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                    FileHash = fileHash,
                };
                nf.GenerateDocumentId(notebookId);
                context.NotebookFiles.Add(nf);
                newFiles.Add(nf);

                await _lineageService.RecordAsync(
                    FileKind.Notebook,
                    notebook.ProjectId,
                    nf.Id,
                    null,
                    FileLineageAction.ExternalWrite,
                    notebookId,
                    fullPath,
                    saveImmediately: false);
            }
        }

        var removed = 0;
        foreach (var dbFile in dbFiles.Values)
        {
            if (physicalFiles.ContainsKey(dbFile.RelativePath))
            {
                continue;
            }

            if (NotebookSyncFileEnumerator.IsUnderRegisteredMount(dbFile.RelativePath, rootPath))
            {
                _logger.LogDebug(
                    "Removing stale indexed row under registered mount during reconcile: {RelativePath}",
                    dbFile.RelativePath);
            }

            var hasContentReferences = await context.ContentFileVersions
                .AnyAsync(v => v.OriginNotebookFileId == dbFile.Id, cancellationToken);
            var hasMessageAttachments = await context.MessageAttachments
                .AnyAsync(ma => ma.NotebookFileId == dbFile.Id, cancellationToken);

            if (!hasContentReferences && !hasMessageAttachments)
            {
                context.NotebookFiles.Remove(dbFile);
                removed++;
            }
            else
            {
                _logger.LogInformation(
                    "Skipped deleting notebook file {NotebookFileId} during reconcile (contentRefs: {ContentRefs}, msgAttachments: {MsgAttachments})",
                    dbFile.Id,
                    hasContentReferences,
                    hasMessageAttachments);
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException dbEx) when (IsDuplicateNotebookFileConstraint(dbEx))
        {
            _logger.LogWarning("Constraint violation during reconcile for notebook {NotebookId}", notebookId);
            return new ReconcileResult();
        }
        catch (DbUpdateConcurrencyException cx)
        {
            _logger.LogWarning(cx, "Concurrency conflict during reconcile for notebook {NotebookId}", notebookId);
            return new ReconcileResult();
        }

        foreach (var newFile in newFiles)
        {
            try
            {
                await _usageRecorder.RecordAsync(
                    projectId: notebook.ProjectId,
                    notebookId: notebookId,
                    category: GuideAnts.Usage.UsageCategory.StorageSystemGenerated,
                    service: "Storage",
                    operation: "system",
                    metrics: new UsageMetrics(ValueOther: newFile.FileSize),
                    notebookFileId: newFile.Id,
                    metadataJson: JsonSerializer.Serialize(new { path = newFile.RelativePath }));
            }
            catch
            {
                // best-effort
            }
        }

        var indexJobsEnqueued = 0;
        foreach (var file in newFiles)
        {
            await EnqueueIndexingJobForFileAsync(context, file, cancellationToken);
            indexJobsEnqueued++;
        }

        foreach (var file in updatedFiles)
        {
            await EnqueueIndexingJobForFileAsync(context, file, cancellationToken);
            indexJobsEnqueued++;
        }

        return new ReconcileResult
        {
            Added = newFiles.Count,
            Updated = updatedFiles.Count,
            Removed = removed,
            IndexJobsEnqueued = indexJobsEnqueued,
        };
    }

    private static bool IsDuplicateNotebookFileConstraint(DbUpdateException exception) =>
        exception.InnerException?.Message?.Contains("IX_NotebookFiles_RelativePath_NotebookId", StringComparison.Ordinal) == true
        || exception.InnerException?.Message?.Contains("IX_NotebookFiles_DocumentId", StringComparison.Ordinal) == true;

    private async Task EnqueueIndexingJobForFileAsync(
        ApplicationDbContext context,
        NotebookFile file,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.RelativePath);
        if (NotebookFileIndexingRules.IsDirectIndexable(extension))
        {
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(IndexDirectTextFileJob).Replace("Job", string.Empty),
                payload: new IndexDirectTextFileJob(file.Id, IsContentFile: false),
                ct: cancellationToken);
            return;
        }

        await EnsureNotebookMarkdownShadowExistsAsync(context, file.Id, cancellationToken);
        await _jobQueueService.EnqueueAsync(
            jobType: nameof(ExtractNotebookFileMarkdownJob).Replace("Job", string.Empty),
            payload: new ExtractNotebookFileMarkdownJob(file.Id),
            ct: cancellationToken);
    }

    private async Task EnsureNotebookMarkdownShadowExistsAsync(
        ApplicationDbContext context,
        Guid notebookFileId,
        CancellationToken cancellationToken)
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
            Status = MarkdownExtractionStatus.Pending,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
        }
    }
}
