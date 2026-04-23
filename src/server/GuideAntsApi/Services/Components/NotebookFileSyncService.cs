using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using System.Security.Claims;
using GuideAntsApi.Services.Core;
using System.Text.Json;

namespace GuideAntsApi.Services.Components;

public class NotebookFileSyncService : INotebookFileSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotebookFileSyncService> _logger;
    private readonly string _storagePath;
    private readonly IFileLineageService _lineageService;
    private readonly IMarkdownExtractionService _markdownExtractionService;
    private readonly GuideAnts.Usage.IUsageRecorder _usageRecorder;
    private readonly INotebookLockService _lockService;
    private readonly IStoragePathResolver _pathResolver;

    public NotebookFileSyncService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<NotebookFileSyncService> logger, IFileLineageService lineageService, IMarkdownExtractionService markdownExtractionService, GuideAnts.Usage.IUsageRecorder usageRecorder, INotebookLockService lockService, IStoragePathResolver pathResolver)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _storagePath = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        _lineageService = lineageService;
        _markdownExtractionService = markdownExtractionService;
        _usageRecorder = usageRecorder;
        _lockService = lockService;
        _pathResolver = pathResolver;
    }

    // Backward-compatible overload used by tests.
    public NotebookFileSyncService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<NotebookFileSyncService> logger, IFileLineageService lineageService, IMarkdownExtractionService markdownExtractionService, GuideAnts.Usage.IUsageRecorder usageRecorder, INotebookLockService lockService)
        : this(
            scopeFactory,
            configuration,
            logger,
            lineageService,
            markdownExtractionService,
            usageRecorder,
            lockService,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")))
    { }

    /// <summary>
    /// Creates a new scope and returns the DbContext. Use with 'using' statement.
    /// </summary>
    private IServiceScope CreateDbScope() => _scopeFactory.CreateScope();

    /// <summary>
    /// Gets the DbContext from a scope. Use with CreateDbScope().
    /// </summary>
    private static ApplicationDbContext GetDbContext(IServiceScope scope) => 
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    public async Task SyncNotebookImmediateAsync(Guid notebookId)
    {
        try
        {
            // Try to acquire lock with 2-second timeout
            await using var lockHandle = await _lockService.TryAcquireAsync(notebookId, TimeSpan.FromSeconds(2));
            
            if (!lockHandle.Acquired)
            {
                // Another sync is already running - skip this one
                _logger.LogDebug("Skipping immediate sync for notebook {NotebookId} - another sync is already running", notebookId);
                return;
            }
            
            // Perform the sync
            await SyncNotebookInternalAsync(notebookId);
        }
        catch (Exception ex)
        {
            // Never throw - just log the error
            _logger.LogError(ex, "Error during immediate sync for notebook {NotebookId}", notebookId);
        }
    }

    public async Task SyncNotebookAsync(Guid notebookId)
    {
        // Legacy entry point - calls internal implementation
        await SyncNotebookInternalAsync(notebookId);
    }

    public async Task QueueNotebookSyncAsync(Guid notebookId, CancellationToken cancellationToken = default)
    {
        using var scope = CreateDbScope();
        var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        await jobQueue.EnqueueAsync(
            jobType: nameof(SyncNotebookJob).Replace("Job", string.Empty),
            payload: new SyncNotebookJob(notebookId),
            ct: cancellationToken);

        _logger.LogDebug("Queued background notebook sync for notebook {NotebookId}", notebookId);
    }

    private async Task SyncNotebookInternalAsync(Guid notebookId)
    {
        // Get notebook and root path
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var notebook = await context.Notebooks.FirstOrDefaultAsync(n => n.Id == notebookId);
        if (notebook == null) return;

        var rootPath = GetNotebookRootPath(notebook.ProjectId, notebook.Id);
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }

        // Get all physical files
        var physicalFiles = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Select(f => NormalizeRelativePath(Path.GetRelativePath(rootPath, f)))
            .Where(f => !IsInGuideantsFolder(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Sync each physical file individually with fresh queries
        foreach (var relativePath in physicalFiles)
        {
            await SyncSingleFileAsync(notebookId, notebook.ProjectId, rootPath, relativePath);
        }

        // Handle deleted files - query DB for files that no longer exist physically
        var dbFiles = await context.NotebookFiles
            .Where(f => f.NotebookId == notebookId)
            .ToListAsync();

		foreach (var dbFile in dbFiles)
		{
			if (!physicalFiles.Contains(dbFile.RelativePath))
			{
				// Do NOT delete notebook files that are still referenced anywhere.
				// Preserve rows when referenced by content versions OR message attachments.
				var hasContentReferences = await context.ContentFileVersions.AnyAsync(v => v.OriginNotebookFileId == dbFile.Id);
				var hasMessageAttachments = await context.MessageAttachments.AnyAsync(ma => ma.NotebookFileId == dbFile.Id);
				
				if (!hasContentReferences && !hasMessageAttachments)
				{
					context.NotebookFiles.Remove(dbFile);
				}
				else
				{
					_logger.LogInformation("Skipped deleting referenced notebook file {NotebookFileId} during sync (contentRefs: {ContentRefs}, msgAttachments: {MsgRefs})", 
						dbFile.Id, hasContentReferences, hasMessageAttachments);
				}
			}
		}

        // Save deletions
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException cx)
        {
            // Benign race: a row we attempted to delete changed between read and save
            _logger.LogWarning(cx, "Concurrency conflict during sync cleanup for notebook {NotebookId}; treating as no-op", notebookId);
        }
    }

    /// <summary>
    /// Syncs a single file with fresh DB query to avoid stale data issues.
    /// </summary>
    private async Task SyncSingleFileAsync(Guid notebookId, Guid projectId, string rootPath, string relativePath)
    {
        var fullPath = Path.Combine(rootPath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        
        // Get file info
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists) return; // File was deleted between enumeration and now
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipped file during sync due to error accessing file info: {FullPath}", fullPath);
            return;
        }

        // Compute hash
        string hash;
        try
        {
            hash = ComputeSha256(fullPath);
        }
        catch (IOException ioEx)
        {
            _logger.LogWarning(ioEx, "Skipped file during sync because it is missing or locked: {FullPath}", fullPath);
            return;
        }
        catch (UnauthorizedAccessException uaEx)
        {
            _logger.LogWarning(uaEx, "Skipped file during sync due to unauthorized access: {FullPath}", fullPath);
            return;
        }

        // Use fresh DB scope and query for THIS file only
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Fresh query for this specific file
        var existing = await context.NotebookFiles
            .FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == relativePath);

        if (existing != null)
        {
            // Update if changed
            if (existing.FileSize != fileInfo.Length || 
                existing.LastModifiedUtc != fileInfo.LastWriteTimeUtc || 
                existing.FileHash != hash)
            {
                existing.FileSize = fileInfo.Length;
                existing.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
                existing.FileHash = hash;

                await _lineageService.RecordAsync(
                    FileKind.Notebook,
                    projectId,
                    existing.Id,
                    null,
                    FileLineageAction.ExternalWrite,
                    notebookId,
                    fullPath,
                    saveImmediately: false);

                try
                {
                    await context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Benign race - file was updated by another operation
                    _logger.LogDebug("Concurrency conflict updating notebook file {RelativePath}, skipping", relativePath);
                }
            }
        }
        else
        {
            // Create new file
            var nf = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = relativePath,
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                FileHash = hash,
            };
            nf.GenerateDocumentId(notebookId);
            context.NotebookFiles.Add(nf);

            await _lineageService.RecordAsync(
                FileKind.Notebook,
                projectId,
                nf.Id,
                null,
                FileLineageAction.ExternalWrite,
                notebookId,
                fullPath,
                saveImmediately: false);

            try
            {
                await context.SaveChangesAsync();
                
                // Record storage usage for system-generated files
                try
                {
                    await _usageRecorder.RecordAsync(
                        projectId: projectId,
                        notebookId: notebookId,
                        category: GuideAnts.Usage.UsageCategory.StorageSystemGenerated,
                        service: "Storage",
                        operation: "system",
                        metrics: new GuideAnts.Usage.UsageMetrics(ValueOther: fileInfo.Length),
                        notebookFileId: nf.Id,
                        metadataJson: JsonSerializer.Serialize(new { path = relativePath }));
                }
                catch { /* best-effort */ }

                // Trigger markdown extraction for newly created file
                await TriggerMarkdownExtractionAsync(nf);
            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message?.Contains("IX_NotebookFiles_RelativePath_NotebookId") == true)
            {
                // Unique constraint violation - file already exists (race condition)
                // This is expected when multiple operations create the same file
                _logger.LogDebug("File {RelativePath} already exists in DB (concurrent insert), skipping", relativePath);
            }
        }
    }

    /// <summary>
    /// Triggers markdown extraction for assistant-created files if they are supported file types
    /// </summary>
    private async Task TriggerMarkdownExtractionAsync(NotebookFile notebookFile)
    {
        try
        {
            var fileName = Path.GetFileName(notebookFile.RelativePath);
            
            // Check if the file type supports markdown extraction or transcription
            var contentType = GetContentTypeFromFileName(fileName);
            
            // Use the same logic as NotebookFileService.UploadFilesAsync
            if (IsMarkdownExtractionSupported(fileName, contentType))
            {
                await _markdownExtractionService.CreateNotebookMarkdownShadowAsync(notebookFile.Id);
                _logger.LogInformation("Triggered markdown extraction for assistant-created file: {RelativePath}", notebookFile.RelativePath);
            }
            else
            {
                _logger.LogDebug("Assistant-created file {RelativePath} does not support markdown extraction", notebookFile.RelativePath);
            }
        }
        catch (Exception ex)
        {
            // Don't let markdown extraction failures break the sync process
            _logger.LogWarning(ex, "Failed to trigger markdown extraction for assistant-created file: {RelativePath}", notebookFile.RelativePath);
        }
    }

    /// <summary>
    /// Determines if a file supports markdown extraction (documents, images, audio, or video)
    /// </summary>
    private static bool IsMarkdownExtractionSupported(string fileName, string contentType)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // Document and image extensions
        var documentExtensions = new[]
        {
            ".pdf", ".docx", ".xlsx", ".pptx", ".html",
            ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".heif"
        };

        // Audio and video extensions  
        var audioVideoExtensions = new[]
        {
            ".wav", ".mp3", ".ogg", ".flac", ".wma", ".aac", ".amr", ".webm", ".opus",
            ".mp4", ".mov", ".avi", ".wmv", ".mkv", ".flv", ".m4v"
        };

        return documentExtensions.Contains(extension) || audioVideoExtensions.Contains(extension);
    }

    /// <summary>
    /// Gets content type from file name extension
    /// </summary>
    private static string GetContentTypeFromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".html" => "text/html",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".tiff" => "image/tiff",
            ".heif" => "image/heif",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".wma" => "audio/x-ms-wma",
            ".aac" => "audio/aac",
            ".amr" => "audio/amr",
            ".webm" => "audio/webm",
            ".opus" => "audio/opus",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".wmv" => "video/x-ms-wmv",
            ".mkv" => "video/x-matroska",
            ".flv" => "video/x-flv",
            ".m4v" => "video/x-m4v",
            _ => "application/octet-stream"
        };
    }

    private string GetNotebookRootPath(Guid projectId, Guid notebookId)
    {
        return _pathResolver.GetNotebookRootPath(projectId, notebookId);
    }

    private static bool IsInGuideantsFolder(string relativePath)
    {
        return relativePath.StartsWith(".guideants/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(".guideants", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace("\\", "/");
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        // Allow reading even if another process still holds a write lock as long as it allows shared reads.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static ClaimsPrincipal CreateSystemPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "system") }));
    }
} 
