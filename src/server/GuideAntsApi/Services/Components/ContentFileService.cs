using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.DataModel.Utilities;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;
using Microsoft.AspNetCore.StaticFiles;
using System.Security.Cryptography;

namespace GuideAntsApi.Services.Components;

public class ContentFileService : IContentFileService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly string _storagePath;
    private readonly IStoragePathResolver _pathResolver;
    private readonly IFileLineageService _lineageService;
    private readonly IMarkdownExtractionService _markdownExtractionService;
    private readonly GuideAnts.Usage.IUsageRecorder _usageRecorder;

    public ContentFileService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IStoragePathResolver pathResolver,
        IFileLineageService lineageService,
        IMarkdownExtractionService markdownExtractionService,
        GuideAnts.Usage.IUsageRecorder usageRecorder)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _storagePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
        _lineageService = lineageService;
        _markdownExtractionService = markdownExtractionService;
        _usageRecorder = usageRecorder;
    }

    // Backward-compatible overload used by tests.
    public ContentFileService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IFileLineageService lineageService,
        IMarkdownExtractionService markdownExtractionService,
        GuideAnts.Usage.IUsageRecorder usageRecorder)
        : this(
            scopeFactory,
            configuration,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")),
            lineageService,
            markdownExtractionService,
            usageRecorder)
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

    private string GetVersionedPhysicalPath(Guid projectId, Guid fileId, int version, string fileName)
    {
        return Path.Combine(_pathResolver.GetProjectRootPath(projectId), "files", fileId.ToString(), $"v{version}", fileName);
    }

    private string GetFileBasePhysicalPath(Guid projectId, Guid fileId)
    {
        return Path.Combine(_pathResolver.GetProjectRootPath(projectId), "files", fileId.ToString());
    }

    // NEW: Content-addressable storage methods
    private string GetContentAddressableStoragePath(Guid projectId, string contentHash)
    {
        return _pathResolver.GetContentAddressablePath(projectId, contentHash);
    }

    private async Task<string> CalculateContentHashAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        stream.Position = 0; // Reset stream position
        var hash = await sha256.ComputeHashAsync(stream);
        stream.Position = 0; // Reset for subsequent reads
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ContentFileDetailsDto ToContentFileDetailsDto(ContentFile file)
    {
        return new ContentFileDetailsDto(
            file.Id,
            file.FileName,
            file.Path,
            file.RelativePath,
            file.ContentType,
            false, // Index removed
            file.DocumentId,
            file.Created,
            file.FileSize,
            file.FolderId,
            file.Folder?.RelativePath,
            file.LatestVersion,
            file.IsSnapshot,

            // Markdown fields - will be populated by enhanced method when needed
            false,
            null,
            null
        );
    }

    public async Task<ContentFileDetailsDto> UploadFileAsync(Guid projectId, IFormFile file, bool index = false, Guid? folderId = null)
    {

// Determine content type
        string? determinedContentType = file.ContentType;
        if (string.IsNullOrWhiteSpace(determinedContentType) || determinedContentType == "application/octet-stream")
        {
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(file.FileName, out determinedContentType))
            {
                determinedContentType = "application/octet-stream";
            }
        }
        
        // Check if a file with the same name already exists in the folder
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var existingFile = await context.ContentFiles
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && f.FolderId == folderId && f.FileName == file.FileName);

        ContentFile contentFile;
        int newVersionNumber;

        if (existingFile != null)
        {
            // --- This is a NEW VERSION of an existing file ---
            contentFile = existingFile;
            contentFile.LatestVersion++;
            newVersionNumber = contentFile.LatestVersion;
            context.ContentFiles.Update(contentFile);
        }
        else
        {
            // --- This is a NEW FILE ---
            newVersionNumber = 1;
            ProjectFolder? targetFolder = null;
            if (folderId.HasValue)
            {
                targetFolder = await context.ProjectFolders
                    .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);
                if (targetFolder == null) throw new ArgumentException("Target folder not found");
            }

            contentFile = new ContentFile
        {
            ProjectId = projectId,
                FileName = file.FileName,
                FileSize = file.Length,
                ContentType = determinedContentType!,
                // Index removed - handled by shadow indexer
                FolderId = folderId,
                Folder = targetFolder,
                LatestVersion = newVersionNumber
            };
            contentFile.UpdateRelativePath();
            contentFile.Path = GetFileBasePhysicalPath(projectId, contentFile.Id);
            contentFile.GenerateDocumentId();
            context.ContentFiles.Add(contentFile);
        }

        // --- Calculate content hash for content-addressable storage ---
        string contentHash;
        using (var stream = file.OpenReadStream())
        {
            contentHash = await CalculateContentHashAsync(stream);
        }
        
        var contentAddressableStoragePath = GetContentAddressableStoragePath(projectId, contentHash);
        
        // --- Store physical file only if it doesn't exist (deduplication) ---
        if (!File.Exists(contentAddressableStoragePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(contentAddressableStoragePath)!);
            using var fileStream = new FileStream(contentAddressableStoragePath, FileMode.Create);
            using var uploadStream = file.OpenReadStream();
            await uploadStream.CopyToAsync(fileStream);
        }

        // --- Create a new file VERSION record ---
        var newVersion = new ContentFileVersion
        {
            ContentFileId = contentFile.Id,
            VersionNumber = newVersionNumber,
            FileName = file.FileName,
            
            // NEW: Content-addressable storage
            ContentHash = contentHash,
            StoragePath = contentAddressableStoragePath,
            OriginalRelativePath = contentFile.RelativePath, // Immutable snapshot
            OriginalFolderId = folderId, // Immutable snapshot
            
            // DEPRECATED: Still populated for backward compatibility during migration
            RelativePath = contentFile.RelativePath,
            Path = GetVersionedPhysicalPath(projectId, contentFile.Id, newVersionNumber, file.FileName),
            
            FileSize = file.Length,
            ContentType = determinedContentType!,
            Indexed = index
        };

        context.ContentFileVersions.Add(newVersion);
        await context.SaveChangesAsync();

        // --- Record lineage event ---
        if (string.IsNullOrWhiteSpace(newVersion.StoragePath))
        {
            throw new InvalidOperationException("ContentFileVersion.StoragePath must be set before recording lineage.");
        }

        await _lineageService.RecordAsync(
            fileKind: FileKind.Project,
            projectId: projectId,
            fileId: contentFile.Id,
            versionNumber: newVersionNumber,
            action: existingFile == null ? FileLineageAction.Uploaded : FileLineageAction.Versioned,
            notebookId: null,
            storagePath: newVersion.StoragePath);

        // --- Record storage usage ---
        try
        {
            await _usageRecorder.RecordAsync(
                projectId: projectId,
                notebookId: Guid.Empty,
                category: GuideAnts.Usage.UsageCategory.StorageUploaded,
                service: "Storage",
                operation: "upload",
                metrics: new GuideAnts.Usage.UsageMetrics(ValueOther: newVersion.FileSize),
                conversationId: null,
                metadataJson: null);
        }
        catch { /* best-effort */ }

        // Index flag ignored – indexing handled by BackgroundJobs after markdown extraction

        // --- Create markdown shadow for supported file types ---
        try
        {
            await _markdownExtractionService.CreateMarkdownShadowAsync(newVersion.Id);
        }
        catch (Exception ex)
        {
            // Log but don't fail the upload if markdown extraction setup fails
            Console.WriteLine($"Failed to create markdown shadow for file {file.FileName}: {ex.Message}");
        }

        // --- Create indexing job for directly indexable files ---
        try
        {
            var extension = Path.GetExtension(file.FileName);
            if (IsDirectIndexable(extension))
            {
                using var jobScope = _scopeFactory.CreateScope();
                var jobQueue = jobScope.ServiceProvider.GetRequiredService<GuideAntsApi.BackgroundJobs.IJobQueueService>();
                await jobQueue.EnqueueAsync(
                    jobType: nameof(GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob).Replace("Job", string.Empty),
                    payload: new GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob(newVersion.Id, IsContentFile: true),
                    ct: CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the upload if indexing job creation fails
            Console.WriteLine($"Failed to create indexing job for file {file.FileName}: {ex.Message}");
        }

        return ToContentFileDetailsDto(contentFile);
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid fileId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var file = await context.ContentFiles
            .Include(f => f.Versions)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId);

        if (file == null)
        {
            return false;
        }

        // Check if any notebook files reference versions of this content file
        // First get the version IDs to check against
        var versionIds = file.Versions.Select(v => v.Id).ToList();
        
        // Then check for notebook files that reference any of these versions
        var notebookUsages = await context.NotebookFiles
            .Include(nf => nf.Notebook)
            .Where(nf => nf.OriginContentFileVersionId.HasValue && 
                        versionIds.Contains(nf.OriginContentFileVersionId.Value))
            .Select(nf => new
            {
                NotebookId = nf.NotebookId,
                NotebookTitle = nf.Notebook.Title,
                NotebookFileId = nf.Id,
                FileName = nf.RelativePath.Contains('/') ? nf.RelativePath.Substring(nf.RelativePath.LastIndexOf('/') + 1) : nf.RelativePath,
                RelativePath = nf.RelativePath
            })
            .ToListAsync();

        if (notebookUsages.Any())
        {
            // File is in use by notebooks - throw a specific exception with details
            var usageDetails = notebookUsages.Select(u => new FileUsageInNotebookDto(
                u.NotebookId,
                u.NotebookTitle,
                u.NotebookFileId,
                u.FileName,
                u.RelativePath
            )).ToList();

            throw new FileInUseException("This file cannot be deleted because it is being used in one or more notebooks. To maintain data traceability, please remove the file from the notebooks first.", usageDetails);
        }

        // KM deletion handled asynchronously by BackgroundJobs in indexing pipeline

        // Delete shadow files before deleting the original files
        foreach (var version in file.Versions)
        {
            // Find and delete associated shadow files
            var shadowFiles = await context.ContentFileMarkdownShadows
                .Where(s => s.OriginalContentFileVersionId == version.Id)
                .ToListAsync();

            foreach (var shadow in shadowFiles)
            {
                // Delete physical shadow file
                if (!string.IsNullOrEmpty(shadow.StoragePath) && File.Exists(shadow.StoragePath))
                {
                    try
                    {
                        File.Delete(shadow.StoragePath);
                        
                        // Clean up empty shadow directories
                        var shadowDir = Path.GetDirectoryName(shadow.StoragePath);
                        if (Directory.Exists(shadowDir) && !Directory.EnumerateFileSystemEntries(shadowDir).Any())
                        {
                            Directory.Delete(shadowDir);
                            // Try to delete parent directories if empty
                            var parentShadowDir = Path.GetDirectoryName(shadowDir);
                            if (Directory.Exists(parentShadowDir) && !Directory.EnumerateFileSystemEntries(parentShadowDir).Any())
                            {
                                Directory.Delete(parentShadowDir);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to delete shadow file {shadow.StoragePath}: {ex.Message}");
                    }
                }
            }
        }

        // Delete all physical file versions - batch query to avoid N+1
        var versionContentHashes = file.Versions
            .Where(v => !string.IsNullOrEmpty(v.ContentHash))
            .Select(v => v.ContentHash!)
            .Distinct()
            .ToList();
        
        // Get reference counts for all content hashes in one query
        var contentHashReferenceCounts = new Dictionary<string, int>();
        if (versionContentHashes.Any())
        {
            var versionIdsToDelete = file.Versions.Select(v => v.Id).ToList();
            var referenceCounts = await context.ContentFileVersions
                .Where(v => versionContentHashes.Contains(v.ContentHash!) && !versionIdsToDelete.Contains(v.Id))
                .GroupBy(v => v.ContentHash)
                .Select(g => new { ContentHash = g.Key, Count = g.Count() })
                .ToListAsync();
            
            foreach (var refCount in referenceCounts)
            {
                contentHashReferenceCounts[refCount.ContentHash!] = refCount.Count;
            }
        }
        
        foreach (var version in file.Versions)
        {
            // Delete from content-addressable storage if it exists
            if (!string.IsNullOrEmpty(version.StoragePath) && File.Exists(version.StoragePath))
            {
                // Only delete if no other versions reference this content hash
                var hasOtherReferences = !string.IsNullOrEmpty(version.ContentHash) && 
                    contentHashReferenceCounts.GetValueOrDefault(version.ContentHash, 0) > 0;
                
                if (!hasOtherReferences)
                {
                    File.Delete(version.StoragePath);
                    
                    // Clean up empty directories
                    var dir = Path.GetDirectoryName(version.StoragePath);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                        // Try to delete parent directories if empty
                        var parentDir = Path.GetDirectoryName(dir);
                        if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                        {
                            Directory.Delete(parentDir);
                        }
                    }
                }
            }
            // Fallback to legacy path deletion
            else if (!string.IsNullOrEmpty(version.Path) && File.Exists(version.Path))
            {
                File.Delete(version.Path);
            }
        }

        // Delete the base directory for the file (legacy storage)
        var baseDir = GetFileBasePhysicalPath(projectId, fileId);
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, true);
        }

        // EF will handle deleting the ContentFileVersion records due to cascading deletes
        // if configured. However, to be explicit:
        context.ContentFileVersions.RemoveRange(file.Versions);
        context.ContentFiles.Remove(file);

        await context.SaveChangesAsync();

        return true;
    }

    public async Task<ContentFileDetailsDto?> GetAsync(Guid projectId, Guid fileId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Use projection to avoid unnecessary entity tracking and improve performance
        var fileDto = await context.ContentFiles
            .Where(f => f.Id == fileId && f.ProjectId == projectId)
            .Select(f => new ContentFileDetailsDto(
                f.Id,
                f.FileName,
                f.Path,
                f.RelativePath,
                f.ContentType,
                false, // Index removed
                f.DocumentId,
                f.Created,
                f.FileSize,
                f.FolderId,
                f.Folder != null ? f.Folder.RelativePath : null,
                f.LatestVersion,
                f.IsSnapshot,
                // Markdown fields - will be populated by enhanced method when needed
                false,
                null,
                null
            ))
            .FirstOrDefaultAsync();

        return fileDto;
    }

    public async Task<IEnumerable<ContentFileDetailsDto>> GetAllForProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Use projection to avoid unnecessary entity tracking and improve performance
        var fileDtos = await context.ContentFiles
            .Where(f => f.ProjectId == projectId)
            .Select(f => new ContentFileDetailsDto(
                f.Id,
                f.FileName,
                f.Path,
                f.RelativePath,
                f.ContentType,
                false, // Index removed
                f.DocumentId,
                f.Created,
                f.FileSize,
                f.FolderId,
                f.Folder != null ? f.Folder.RelativePath : null,
                f.LatestVersion,
                f.IsSnapshot,
                // Markdown fields - will be populated by enhanced method when needed
                false,
                null,
                null
            ))
            .ToListAsync();

        return fileDtos;
    }

    public async Task<ContentFileDetailsDto?> UpdateAsync(Guid projectId, Guid fileId, UpdateContentFileDto updates)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var file = await context.ContentFiles
            .Include(f => f.Folder)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId);

        if (file == null)
        {
            return null;
        }

        // Handle filename updates
        if (!string.IsNullOrWhiteSpace(updates.FileName) && updates.FileName != file.FileName)
        {
            // Validate filename
            if (updates.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Invalid characters in filename");
            }

            // Check for conflicts in the same folder
            var conflictingFile = await context.ContentFiles
                .FirstOrDefaultAsync(f => f.ProjectId == projectId && 
                                         f.FolderId == file.FolderId && 
                                         f.FileName == updates.FileName &&
                                         f.Id != fileId);

            if (conflictingFile != null)
            {
                throw new ArgumentException($"A file named '{updates.FileName}' already exists in this location");
            }

            var oldFileName = file.FileName;
            file.FileName = updates.FileName;
            file.UpdateRelativePath();

            // Record lineage event
            var latestVersion = await context.ContentFileVersions
                .Where(v => v.ContentFileId == fileId)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync();

            if (latestVersion != null)
            {
                var storagePath = !string.IsNullOrEmpty(latestVersion.StoragePath) ? latestVersion.StoragePath : latestVersion.Path;
                await _lineageService.RecordAsync(
            FileKind.Project,
                    projectId,
                    file.Id,
                    file.LatestVersion,
                    FileLineageAction.Renamed,
                    null,
                    storagePath ?? string.Empty);
            }
        }

        await context.SaveChangesAsync();

        return ToContentFileDetailsDto(file);
    }

    public async Task<ContentFileContentDto?> GetContentAsync(Guid projectId, Guid fileId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var file = await context.ContentFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId);

        if (file == null)
        {
            return null;
        }

        return await GetVersionContentAsync(projectId, fileId, file.LatestVersion);
    }

    public async Task<ContentFileDetailsDto?> MoveFileAsync(Guid projectId, Guid fileId, Guid? destinationFolderId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var destinationFolder = destinationFolderId.HasValue
            ? await context.ProjectFolders.FindAsync(destinationFolderId)
            : null;

        if (destinationFolderId.HasValue && destinationFolder == null)
        {
            throw new ArgumentException("Destination folder not found");
        }

        var fileToMove = await context.ContentFiles
            .Include(f => f.Folder)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId);

        if (fileToMove == null)
        {
            return null;
        }

        // Check for duplicate file names in the destination
        var newFileName = fileToMove.FileName;
        var conflictingFile = await context.ContentFiles
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && 
                                     f.FolderId == destinationFolderId && 
                                      f.FileName == newFileName &&
                                      f.Id != fileId);

        if (conflictingFile != null)
        {
            throw new ArgumentException($"A file with the name '{newFileName}' already exists in the destination folder.");
        }

        fileToMove.FolderId = destinationFolderId;
        fileToMove.Folder = destinationFolder;

        var oldRelativePath = fileToMove.RelativePath;
        fileToMove.UpdateRelativePath();
        var newRelativePath = fileToMove.RelativePath;

        // CRITICAL FIX: Do NOT modify ContentFileVersion records!
        // Version records must remain immutable to preserve lineage.
        // Only the ContentFile's current location should be updated.
        
        // TODO: Once content-addressable storage is implemented,
        // version records will have OriginalRelativePath that never changes.

        // Record lineage event for the move
        var latestVersion = await context.ContentFileVersions
            .Where(v => v.ContentFileId == fileId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync();

        if (latestVersion != null)
        {
            var storagePath = !string.IsNullOrEmpty(latestVersion.StoragePath) ? latestVersion.StoragePath : latestVersion.Path;
            await _lineageService.RecordAsync(
            FileKind.Project,
                projectId,
                fileToMove.Id,
                fileToMove.LatestVersion,
                FileLineageAction.Moved,
                null,
                storagePath ?? string.Empty);
        }

        await context.SaveChangesAsync();

        return ToContentFileDetailsDto(fileToMove);
    }

    public async Task<IEnumerable<ContentFileVersionDto>> GetVersionsAsync(Guid projectId, Guid fileId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var versions = await context.ContentFileVersions
            .AsNoTracking()
            .Where(v => v.ContentFileId == fileId && v.ContentFile.ProjectId == projectId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new ContentFileVersionDto(
                v.Id,
                v.VersionNumber,
                v.FileName,
                v.FileSize,
                v.ContentType,
                v.Created,
                v.FromNotebook,
                v.OriginNotebookId,
                v.OriginalRelativePath ?? v.RelativePath, // Fallback for legacy records
                v.OriginalFolderId,
                v.ContentHash
            ))
            .ToListAsync();

        return versions;
    }

    public async Task<ContentFileContentDto?> GetVersionContentAsync(Guid projectId, Guid fileId, int versionNumber)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var version = await context.ContentFileVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.ContentFileId == fileId && v.VersionNumber == versionNumber && v.ContentFile.ProjectId == projectId);

        if (version == null)
        {
            return null;
        }

        // Resolve physical path with compatibility fallback so legacy GUID/relative records
        // still work after slug-based migration.
        string? physicalPath = null;
        if (!string.IsNullOrWhiteSpace(version.StoragePath) &&
            StoragePathCompatibility.TryResolveExistingFilePath(version.StoragePath, _storagePath, out var resolvedStoragePath))
        {
            physicalPath = resolvedStoragePath;
        }
        else if (StoragePathCompatibility.TryResolveExistingFilePath(version.Path, _storagePath, out var resolvedVersionPath))
        {
            physicalPath = resolvedVersionPath;
        }
        else if (!string.IsNullOrWhiteSpace(version.ContentHash))
        {
            var recomputedPath = GetContentAddressableStoragePath(projectId, version.ContentHash);
            if (File.Exists(recomputedPath))
            {
                physicalPath = recomputedPath;
            }
        }
        else
        {
            var recomputedVersionedPath = GetVersionedPhysicalPath(projectId, fileId, versionNumber, version.FileName);
            if (File.Exists(recomputedVersionedPath))
            {
                physicalPath = recomputedVersionedPath;
            }
        }

        if (string.IsNullOrWhiteSpace(physicalPath) || !File.Exists(physicalPath))
        {
            return null;
        }

        // Stream the file content instead of loading it entirely into memory
        var fileInfo = new FileInfo(physicalPath);
        var content = new byte[fileInfo.Length];
        
        await using var fileStream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await fileStream.ReadExactlyAsync(content, 0, (int)fileInfo.Length);

        return new ContentFileContentDto
        {
            Content = content,
            ContentType = version.ContentType,
            FileName = version.FileName
        };
    }

    public async Task<ContentFileDetailsDto> CreateFileFromPathAsync(Guid projectId, string sourcePath, string fileName, string contentType, Guid? folderId, Guid originNotebookFileId, bool index = false)
    {

if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source notebook file was not found on the server.", sourcePath);
        }

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var existingFile = await context.ContentFiles
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && f.FolderId == folderId && f.FileName == fileName);

        if (existingFile != null)
        {
            return await CreateVersionFromPathAsync(projectId, existingFile.Id, sourcePath, originNotebookFileId, index);
        }

        ProjectFolder? targetFolder = null;
        if (folderId.HasValue)
        {
            targetFolder = await context.ProjectFolders.FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);
            if (targetFolder == null) throw new ArgumentException("Target folder not found");
        }

        var fileInfo = new FileInfo(sourcePath);

        var contentFile = new ContentFile
        {
            ProjectId = projectId,
            FileName = fileName,
            FileSize = fileInfo.Length,
            ContentType = contentType,
            // Index removed - handled by shadow indexer
            FolderId = folderId,
            Folder = targetFolder,
            LatestVersion = 1
        };
        contentFile.UpdateRelativePath();
        contentFile.GenerateDocumentId();
        context.ContentFiles.Add(contentFile);

        string contentHash;
        await using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            contentHash = await CalculateContentHashAsync(stream);
        }

        var storagePath = GetContentAddressableStoragePath(projectId, contentHash);

        if (!File.Exists(storagePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            File.Copy(sourcePath, storagePath);
        }

        var newVersion = new ContentFileVersion
        {
            ContentFileId = contentFile.Id,
            VersionNumber = 1,
            FileName = fileName,
            ContentHash = contentHash,
            StoragePath = storagePath,
            OriginalRelativePath = contentFile.RelativePath,
            OriginalFolderId = folderId,
            FileSize = contentFile.FileSize,
            ContentType = contentType,
            Indexed = index,
            OriginNotebookFileId = originNotebookFileId,
            FromNotebook = true
        };
        context.ContentFileVersions.Add(newVersion);

        await context.SaveChangesAsync();

        // Get notebook ID from the origin notebook file
        var originNotebookFile = await context.NotebookFiles.FindAsync(originNotebookFileId);
        var notebookId = originNotebookFile?.NotebookId;
        await _lineageService.RecordAsync(
            FileKind.Project, projectId, contentFile.Id, 1, FileLineageAction.PublishedToProject, null, storagePath);

        // Index flag ignored – indexing handled by BackgroundJobs after markdown extraction

        // --- Create markdown shadow for supported file types ---
        try
        {
            await _markdownExtractionService.CreateMarkdownShadowAsync(newVersion.Id);
        }
        catch (Exception ex)
        {
            // Log but don't fail the operation if markdown extraction setup fails
            Console.WriteLine($"Failed to create markdown shadow for published file {fileName}: {ex.Message}");
        }

        // --- Create indexing job for directly indexable files ---
        try
        {
            var extension = Path.GetExtension(fileName);
            if (IsDirectIndexable(extension))
            {
                using var jobScope = _scopeFactory.CreateScope();
                var jobQueue = jobScope.ServiceProvider.GetRequiredService<GuideAntsApi.BackgroundJobs.IJobQueueService>();
                await jobQueue.EnqueueAsync(
                    jobType: nameof(GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob).Replace("Job", string.Empty),
                    payload: new GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob(newVersion.Id, IsContentFile: true),
                    ct: CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the operation if indexing job creation fails
            Console.WriteLine($"Failed to create indexing job for published file {fileName}: {ex.Message}");
        }

        return ToContentFileDetailsDto(contentFile);
    }

    public async Task<ContentFileDetailsDto> CreateVersionFromPathAsync(Guid projectId, Guid contentFileId, string sourcePath, Guid originNotebookFileId, bool index = false)
    {

if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source notebook file was not found on the server.", sourcePath);
        }

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var contentFile = await context.ContentFiles.FindAsync(contentFileId);
        if (contentFile == null || contentFile.ProjectId != projectId)
        {
            throw new ArgumentException("The specified content file does not exist in this project.");
        }

        var fileInfo = new FileInfo(sourcePath);

        contentFile.LatestVersion++;
        int newVersionNumber = contentFile.LatestVersion;
        context.ContentFiles.Update(contentFile);

        string contentHash;
        await using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            contentHash = await CalculateContentHashAsync(stream);
        }

        var storagePath = GetContentAddressableStoragePath(projectId, contentHash);

        if (!File.Exists(storagePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            File.Copy(sourcePath, storagePath);
        }

        var newVersion = new ContentFileVersion
        {
            ContentFileId = contentFile.Id,
            VersionNumber = newVersionNumber,
            FileName = contentFile.FileName,
            ContentHash = contentHash,
            StoragePath = storagePath,
            OriginalRelativePath = contentFile.RelativePath,
            OriginalFolderId = contentFile.FolderId,
            FileSize = fileInfo.Length,
            ContentType = contentFile.ContentType,
            Indexed = index,
            OriginNotebookFileId = originNotebookFileId,
            FromNotebook = true
        };
        context.ContentFileVersions.Add(newVersion);
        await context.SaveChangesAsync();

        // Get notebook ID from the origin notebook file
        var originNotebookFile = await context.NotebookFiles.FindAsync(originNotebookFileId);
        var notebookId = originNotebookFile?.NotebookId;
        await _lineageService.RecordAsync(
            FileKind.Project, projectId, contentFile.Id, newVersionNumber, FileLineageAction.PublishedToProject, null, storagePath);

        // Index flag ignored – indexing handled by BackgroundJobs after markdown extraction

        // --- Create markdown shadow for supported file types ---
        try
        {
            await _markdownExtractionService.CreateMarkdownShadowAsync(newVersion.Id);
        }
        catch (Exception ex)
        {
            // Log but don't fail the operation if markdown extraction setup fails
            Console.WriteLine($"Failed to create markdown shadow for published file {contentFile.FileName}: {ex.Message}");
        }

        // --- Create indexing job for directly indexable files ---
        try
        {
            var extension = Path.GetExtension(contentFile.FileName);
            if (IsDirectIndexable(extension))
            {
                using var jobScope = _scopeFactory.CreateScope();
                var jobQueue = jobScope.ServiceProvider.GetRequiredService<GuideAntsApi.BackgroundJobs.IJobQueueService>();
                await jobQueue.EnqueueAsync(
                    jobType: nameof(GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob).Replace("Job", string.Empty),
                    payload: new GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob(newVersion.Id, IsContentFile: true),
                    ct: CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the operation if indexing job creation fails
            Console.WriteLine($"Failed to create indexing job for published file {contentFile.FileName}: {ex.Message}");
        }

        return ToContentFileDetailsDto(contentFile);
    }

    private static bool IsDirectIndexable(string extension)
    {
        var directIndexableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".json", ".xml", ".puml", ".yaml", ".yml", ".csv", ".sql", ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".h"
        };
        return directIndexableExtensions.Contains(extension);
    }
} 
