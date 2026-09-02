using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public class ProjectFolderService : IProjectFolderService
{
    private const int DefaultLinkedMountTreeMaxFiles = 5000;
    private const int DefaultLinkedMountTreeMaxDepth = 3;
    private const int DefaultLinkedMountTreeScanBudgetMs = 2500;

    // The project folder tree is polled roughly once per minute per viewer. The mount
    // scan cache TTL must comfortably exceed that poll interval, otherwise the cache
    // expires in every gap between polls and each poll re-walks the (potentially huge)
    // host directory. Project files change through our own API (uploads/deletes are
    // DB-backed and known to the server), so unlike notebooks we don't need to re-scan
    // aggressively to discover out-of-band changes; a couple of minutes bounds staleness
    // for the read-only host-mount overlay while cutting the scan rate dramatically.
    private const int DefaultProjectMountTreeCacheSeconds = 120;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly string _storagePath;
    private readonly IStoragePathResolver _pathResolver;
    private readonly ILogger<ProjectFolderService> _logger;
    private readonly int _linkedMountTreeMaxFiles;
    private readonly int _linkedMountTreeMaxDepth;
    private readonly TimeSpan _linkedMountTreeScanBudget;
    private readonly TimeSpan _projectMountTreeCacheTtl;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

    public ProjectFolderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IStoragePathResolver pathResolver,
        ILogger<ProjectFolderService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _logger = logger;
        _storagePath = _configuration["FileStorage:Path"] ??
            throw new InvalidOperationException("FileStorage:Path is not configured");
        _linkedMountTreeMaxFiles = ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeMaxFiles"],
            DefaultLinkedMountTreeMaxFiles, min: 1, max: 100000);
        _linkedMountTreeMaxDepth = ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeMaxDepth"],
            DefaultLinkedMountTreeMaxDepth, min: 1, max: 32);
        _linkedMountTreeScanBudget = TimeSpan.FromMilliseconds(ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeScanBudgetMs"],
            DefaultLinkedMountTreeScanBudgetMs, min: 250, max: 30000));
        _projectMountTreeCacheTtl = TimeSpan.FromSeconds(ReadPositiveInt(
            configuration["FileStorage:ProjectMountTreeCacheSeconds"],
            DefaultProjectMountTreeCacheSeconds, min: 1, max: 3600));
    }

    // Backward-compatible overload used by tests.
    public ProjectFolderService(IServiceScopeFactory scopeFactory, IConfiguration configuration)
        : this(
            scopeFactory,
            configuration,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")),
            NullLogger<ProjectFolderService>.Instance)
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

    private static ProjectFolderDto ToProjectFolderDto(ProjectFolder folder, int fileCount = 0, int subFolderCount = 0)
    {
        return new ProjectFolderDto(
            folder.Id,
            folder.Name,
            folder.RelativePath,
            folder.ParentFolderId,
            folder.Created,
            folder.Modified,
            fileCount,
            subFolderCount
        );
    }

    private static FolderTreeDto ToFolderTreeDto(ProjectFolder folder, List<FolderTreeDto> subFolders, List<ContentFileDetailsDto> files)
    {
        return new FolderTreeDto(
            folder.Id,
            folder.Name,
            folder.RelativePath,
            subFolders,
            files
        );
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

    public async Task<ProjectFolderDto> CreateFolderAsync(Guid projectId, CreateFolderDto dto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        // Validate folder name
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid folder name");
        }

        // Calculate relative path
        string relativePath = dto.Name;
        ProjectFolder? parentFolder = null;
        
        if (dto.ParentFolderId.HasValue)
        {
            parentFolder = await context.ProjectFolders
                .FirstOrDefaultAsync(f => f.Id == dto.ParentFolderId && f.ProjectId == projectId);
            
            if (parentFolder == null)
            {
                throw new ArgumentException("Parent folder not found");
            }
            
            relativePath = string.IsNullOrEmpty(parentFolder.RelativePath) 
                ? dto.Name 
                : $"{parentFolder.RelativePath}/{dto.Name}";
        }

        // Check for duplicate path
        var existingFolder = await context.ProjectFolders
            .FirstOrDefaultAsync(f => f.RelativePath == relativePath && f.ProjectId == projectId);
        
        if (existingFolder != null)
        {
            throw new ArgumentException("Folder already exists at this path");
        }

        // Create physical directory
        var physicalPath = Path.Combine(_pathResolver.GetProjectRootPath(projectId), relativePath);
        Directory.CreateDirectory(physicalPath);

        // Create database record
        var folder = new ProjectFolder
        {
            Name = dto.Name,
            RelativePath = relativePath,
            ProjectId = projectId,
            ParentFolderId = dto.ParentFolderId
        };

        context.ProjectFolders.Add(folder);
        await context.SaveChangesAsync();

        return ToProjectFolderDto(folder, 0, 0);
    }

    public async Task<ProjectFolderDto?> UpdateFolderAsync(Guid projectId, Guid folderId, UpdateFolderDto dto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Include(f => f.SubFolders)
            .Include(f => f.ContentFiles)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);

        if (folder == null)
        {
            return null;
        }

        var oldPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        var oldRelativePath = folder.RelativePath;
        bool pathChanged = false;

        // Update name if provided
        if (!string.IsNullOrWhiteSpace(dto.Name) && dto.Name != folder.Name)
        {
            if (dto.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Invalid folder name");
            }

            folder.Name = dto.Name;
            pathChanged = true;
        }

        // Update parent if provided
        if (dto.ParentFolderId != folder.ParentFolderId)
        {
            // Validate parent folder exists and prevent circular references
            if (dto.ParentFolderId.HasValue)
            {
                var newParent = await context.ProjectFolders
                    .FirstOrDefaultAsync(f => f.Id == dto.ParentFolderId && f.ProjectId == projectId);

                if (newParent == null)
                {
                    throw new ArgumentException("Parent folder not found");
                }

                if (!folder.CanMoveTo(newParent))
                {
                    throw new ArgumentException("Cannot move folder to a subfolder of itself");
                }
            }

            folder.ParentFolderId = dto.ParentFolderId;
            pathChanged = true;
        }

        if (pathChanged)
        {
            // Update relative path
            var parentFolder = dto.ParentFolderId.HasValue 
                ? await context.ProjectFolders.FirstOrDefaultAsync(f => f.Id == dto.ParentFolderId)
                : null;
            
            folder.UpdateRelativePath(parentFolder);

            // Check for path conflicts
            var conflictingFolder = await context.ProjectFolders
                .FirstOrDefaultAsync(f => f.RelativePath == folder.RelativePath && f.ProjectId == projectId && f.Id != folderId);
            
            if (conflictingFolder != null)
            {
                throw new ArgumentException("A folder already exists at this path");
            }

            // Move physical directory
            var newPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
            if (Directory.Exists(oldPhysicalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                Directory.Move(oldPhysicalPath, newPhysicalPath);
            }

            // Update all descendant folders and files
            await UpdateDescendantPaths(context, folder, oldRelativePath);
        }

        await context.SaveChangesAsync();

        var fileCount = await context.ContentFiles.CountAsync(f => f.FolderId == folderId);
        var subFolderCount = await context.ProjectFolders.CountAsync(f => f.ParentFolderId == folderId);

        return ToProjectFolderDto(folder, fileCount, subFolderCount);
    }

    public async Task<bool> DeleteFolderAsync(Guid projectId, Guid folderId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Include(f => f.SubFolders)
            .Include(f => f.ContentFiles)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);

        if (folder == null)
        {
            return false;
        }

        // Only allow deletion of empty folders
        if (!folder.IsEmpty())
        {
            throw new InvalidOperationException("Cannot delete non-empty folder");
        }

        // Delete physical directory if it exists
        var physicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        if (Directory.Exists(physicalPath))
        {
            Directory.Delete(physicalPath);
        }

        context.ProjectFolders.Remove(folder);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<IEnumerable<ProjectFolderDto>> GetFoldersAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folders = await context.ProjectFolders
            .Where(f => f.ProjectId == projectId)
            .Select(f => new { Folder = f, FileCount = f.ContentFiles.Count, SubFolderCount = f.SubFolders.Count })
            .ToListAsync();

        return folders.Select(f => ToProjectFolderDto(f.Folder, f.FileCount, f.SubFolderCount));
    }

    public async Task<FolderTreeDto> GetFolderTreeAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        // Retrieve the project name to use as the root folder title
        var projectName = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Title)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = "Root";
        }

        // Get all folders and files for the project
        var folders = await context.ProjectFolders
            .Where(f => f.ProjectId == projectId)
            .Include(f => f.SubFolders)
            .Include(f => f.ContentFiles)
            .ToListAsync();

        var files = await context.ContentFiles
            .Where(f => f.ProjectId == projectId)
            .Include(f => f.Folder)
            .ToListAsync();

        // Build tree structure
        var tree = BuildFolderTree(folders, files, null, projectName);

        // Overlay project-scope host folder mounts
        var projectMounts = await context.HostFolderMounts
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId
                && m.Scope == HostFolderMountScope.Project
                && m.Status != HostFolderMountStatus.Removed
                && m.Status != HostFolderMountStatus.PendingRemoval)
            .ToListAsync();

        if (projectMounts.Count > 0)
        {
            var mountFolders = BuildMountOverlayNodes(projectMounts);
            tree.SubFolders.AddRange(mountFolders);
        }

        return tree;
    }

    public async Task<bool> MoveFolderAsync(Guid projectId, Guid folderId, Guid? newParentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);

        if (folder == null)
        {
            return false;
        }

        // Validate new parent
        ProjectFolder? newParent = null;
        if (newParentId.HasValue)
        {
            newParent = await context.ProjectFolders
                .FirstOrDefaultAsync(f => f.Id == newParentId && f.ProjectId == projectId);
            
            if (newParent == null)
            {
                return false;
            }

            if (!folder.CanMoveTo(newParent))
            {
                return false;
            }
        }

        var oldPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        var oldRelativePath = folder.RelativePath;

        // Update parent and path
        folder.ParentFolderId = newParentId;
        folder.UpdateRelativePath(newParent);

        // Check for path conflicts
        var conflictingFolder = await context.ProjectFolders
            .FirstOrDefaultAsync(f => f.RelativePath == folder.RelativePath && f.ProjectId == projectId && f.Id != folderId);
        
        if (conflictingFolder != null)
        {
            return false;
        }

        // Move physical directory
        var newPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        if (Directory.Exists(oldPhysicalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
            Directory.Move(oldPhysicalPath, newPhysicalPath);
        }

        // Update descendant paths
        await UpdateDescendantPaths(context, folder, oldRelativePath);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<ProjectFolderDto?> GetFolderAsync(Guid projectId, Guid folderId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Where(f => f.Id == folderId && f.ProjectId == projectId)
            .Select(f => new { Folder = f, FileCount = f.ContentFiles.Count, SubFolderCount = f.SubFolders.Count })
            .FirstOrDefaultAsync();

        return folder == null ? null : ToProjectFolderDto(folder.Folder, folder.FileCount, folder.SubFolderCount);
    }

    private async Task UpdateDescendantPaths(ApplicationDbContext context, ProjectFolder folder, string oldBasePath)
    {
        var descendants = await context.ProjectFolders
            .Where(f => f.ProjectId == folder.ProjectId && f.RelativePath.StartsWith(oldBasePath + "/"))
            .ToListAsync();

        foreach (var descendant in descendants)
        {
            var relativePart = descendant.RelativePath.Substring(oldBasePath.Length + 1);
            descendant.RelativePath = $"{folder.RelativePath}/{relativePart}";
            descendant.Modified = DateTime.UtcNow;
        }

        var descendantFiles = await context.ContentFiles
            .Where(f => f.ProjectId == folder.ProjectId && f.RelativePath.StartsWith(oldBasePath + "/"))
            .ToListAsync();

        foreach (var file in descendantFiles)
        {
            var relativePart = file.RelativePath.Substring(oldBasePath.Length + 1);
            file.RelativePath = $"{folder.RelativePath}/{relativePart}";
            
            // Update physical path
            var oldPhysicalPath = file.Path;
            var newPhysicalPath = Path.Combine(_pathResolver.GetProjectRootPath(folder.ProjectId), file.RelativePath);
            
            if (File.Exists(oldPhysicalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                File.Move(oldPhysicalPath, newPhysicalPath);
                file.Path = newPhysicalPath;
            }
        }
    }

    private static FolderTreeDto BuildFolderTree(List<ProjectFolder> allFolders, List<ContentFile> allFiles, Guid? parentId, string rootName)
    {
        var rootFolders = allFolders.Where(f => f.ParentFolderId == parentId).ToList();
        var rootFiles = allFiles.Where(f => f.FolderId == parentId).ToList();

        // Create virtual root folder for the project
        if (parentId == null)
        {
            var subFolders = rootFolders.Select(f => BuildFolderTreeNode(f, allFolders, allFiles)).ToList();
            var files = rootFiles.Select(ToContentFileDetailsDto).ToList();

            return new FolderTreeDto(
                Guid.Empty, // Virtual root
                rootName,
                "",
                subFolders,
                files
            );
        }

        var folder = allFolders.First(f => f.Id == parentId);
        return BuildFolderTreeNode(folder, allFolders, allFiles);
    }

    private static FolderTreeDto BuildFolderTreeNode(ProjectFolder folder, List<ProjectFolder> allFolders, List<ContentFile> allFiles)
    {
        var subFolders = allFolders
            .Where(f => f.ParentFolderId == folder.Id)
            .Select(f => BuildFolderTreeNode(f, allFolders, allFiles))
            .ToList();

        var files = allFiles
            .Where(f => f.FolderId == folder.Id)
            .Select(ToContentFileDetailsDto)
            .ToList();

        return ToFolderTreeDto(folder, subFolders, files);
    }

    // ---- Mounted-file read/write by relativePath ----

    private async Task<(HostFolderMount Mount, string PhysicalPath, string ContentType, string FileName)?> ResolveMountedFilePhysicalPathAsync(Guid projectId, string relativePath)
    {
        var normalized = relativePath.Replace("\\", "/").TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var segments = normalized.Split('/');
        if (segments.Length < 2)
            return null;

        var leafName = segments[0];

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        var mounts = await context.HostFolderMounts
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId
                && m.Scope == HostFolderMountScope.Project
                && m.Status != HostFolderMountStatus.Removed
                && m.Status != HostFolderMountStatus.PendingRemoval)
            .ToListAsync();

        var mount = mounts.FirstOrDefault(m =>
            string.Equals(m.LeafName, leafName, StringComparison.OrdinalIgnoreCase));
        if (mount == null)
            return null;

        var rest = normalized[(leafName.Length + 1)..];
        if (string.IsNullOrWhiteSpace(rest))
            return null;

        var fullRoot = Path.GetFullPath(mount.ContainerSourcePath);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, rest));

        var sep = Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(fullRoot + sep, StringComparison.Ordinal) && candidate != fullRoot)
            return null;

        _contentTypeProvider.TryGetContentType(Path.GetFileName(candidate), out var ct);
        return (mount, candidate, ct ?? "application/octet-stream", Path.GetFileName(candidate));
    }

    public async Task<(Stream Stream, string ContentType, string FileName)?> GetMountedFileContentAsync(Guid projectId, string relativePath)
    {
        var resolved = await ResolveMountedFilePhysicalPathAsync(projectId, relativePath);
        if (resolved == null || !File.Exists(resolved.Value.PhysicalPath))
            return null;

        var stream = new FileStream(resolved.Value.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (stream, resolved.Value.ContentType, resolved.Value.FileName);
    }

    public async Task<ContentFileDetailsDto?> GetMountedFileDetailsAsync(Guid projectId, string relativePath)
    {
        var resolved = await ResolveMountedFilePhysicalPathAsync(projectId, relativePath);
        if (resolved == null || !File.Exists(resolved.Value.PhysicalPath))
            return null;

        var info = new FileInfo(resolved.Value.PhysicalPath);
        return new ContentFileDetailsDto(
            Id: CreateMountVirtualFileId(resolved.Value.Mount.Id, relativePath),
            FileName: resolved.Value.FileName,
            Path: "",
            RelativePath: relativePath.Replace("\\", "/"),
            ContentType: resolved.Value.ContentType,
            Index: false,
            DocumentId: "",
            Created: info.LastWriteTimeUtc,
            FileSize: info.Length,
            FolderId: null,
            FolderPath: null,
            LatestVersion: 0,
            IsSnapshot: false,
            HasMarkdownShadow: false,
            MarkdownStatus: null,
            MarkdownProcessedAt: null);
    }

    public async Task<bool> SaveMountedFileContentAsync(Guid projectId, string relativePath, Stream content)
    {
        var resolved = await ResolveMountedFilePhysicalPathAsync(projectId, relativePath);
        if (resolved == null)
            return false;

        var dir = Path.GetDirectoryName(resolved.Value.PhysicalPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var fs = new FileStream(resolved.Value.PhysicalPath, FileMode.Create);
        await content.CopyToAsync(fs);
        return true;
    }

    public async Task<bool> RenameMountedEntryAsync(Guid projectId, string relativePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return false;

        var safeName = newName.Trim();
        if (safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || safeName.Contains('/')
            || safeName.Contains('\\')
            || safeName == "."
            || safeName == "..")
            return false;

        var resolved = await ResolveMountedFilePhysicalPathAsync(projectId, relativePath);
        if (resolved == null)
            return false;

        var sourceDir = Path.GetDirectoryName(resolved.Value.PhysicalPath);
        if (string.IsNullOrEmpty(sourceDir))
            return false;

        var newPhysicalPath = Path.GetFullPath(Path.Combine(sourceDir, safeName));

        // Validate the new path stays under the mount root
        var fullRoot = Path.GetFullPath(resolved.Value.Mount.ContainerSourcePath);
        var sep = Path.DirectorySeparatorChar;
        if (!newPhysicalPath.StartsWith(fullRoot + sep, StringComparison.Ordinal) && newPhysicalPath != fullRoot)
            return false;

        if (newPhysicalPath == resolved.Value.PhysicalPath)
            return true;

        if (File.Exists(newPhysicalPath) || Directory.Exists(newPhysicalPath))
            return false;

        if (File.Exists(resolved.Value.PhysicalPath))
            File.Move(resolved.Value.PhysicalPath, newPhysicalPath);
        else if (Directory.Exists(resolved.Value.PhysicalPath))
            Directory.Move(resolved.Value.PhysicalPath, newPhysicalPath);
        else
            return false;

        return true;
    }

    private List<FolderTreeDto> BuildMountOverlayNodes(List<HostFolderMount> mounts)
    {
        var mountNodes = new List<FolderTreeDto>();

        foreach (var mount in mounts)
        {
            var scanResult = GetOrScanMount(mount);
            var mountRootNode = BuildMountFolderTree(mount, scanResult.Files, scanResult.Directories);
            mountNodes.Add(mountRootNode);
        }

        return mountNodes;
    }

    public async Task<HostMountListingDto?> ListHostMountLevelAsync(Guid projectId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        var mounts = await context.HostFolderMounts
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId
                && m.Scope == HostFolderMountScope.Project
                && m.Status != HostFolderMountStatus.Removed
                && m.Status != HostFolderMountStatus.PendingRemoval)
            .ToListAsync();

        var normalizedPath = relativePath.Replace('\\', '/').Trim('/');
        var mount = mounts.FirstOrDefault(m =>
            normalizedPath.Equals(m.LeafName, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(m.LeafName + "/", StringComparison.OrdinalIgnoreCase));
        if (mount == null)
        {
            return null;
        }

        var withinMount = normalizedPath.Equals(mount.LeafName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalizedPath[(mount.LeafName.Length + 1)..];

        var mountKey = mount.Id.ToString("N");
        var cacheKey = HostMountListingCache.LevelKey(mountKey, normalizedPath);
        var scanResult = HostMountListingCache.GetOrAdd(
            cacheKey,
            () =>
            {
                var listed = HostMountDirectoryScanner.ListLevel(
                    mount.ContainerSourcePath,
                    mount.LeafName,
                    withinMount,
                    _linkedMountTreeMaxFiles,
                    _linkedMountTreeScanBudget,
                    _logger);
                _logger.LogInformation(
                    "Project host mount lazy_list for mount {MountId} path {Path} (files={FileCount}, dirs={DirCount}, truncated={Truncated})",
                    mount.Id,
                    normalizedPath,
                    listed.Files.Count,
                    listed.Directories.Count,
                    listed.WasTruncated);
                return listed;
            },
            _projectMountTreeCacheTtl);

        var folders = scanResult.Directories
            .Select(d => new HostMountListingFolderDto(d.Name, d.RelativePath))
            .ToList();
        var files = scanResult.Files
            .Select(f => new HostMountListingFileDto(
                Id: CreateMountVirtualFileId(mount.Id, f.RelativePath),
                FileName: f.FileName,
                RelativePath: f.RelativePath,
                FileSize: f.FileSize,
                LastModifiedUtc: f.LastModifiedUtc,
                FileHash: $"{f.FileSize:x}-{f.LastModifiedUtc.Ticks:x}",
                IsLinked: true))
            .ToList();

        return new HostMountListingDto(normalizedPath, folders, files, scanResult.WasTruncated);
    }

    /// <summary>
    /// Returns the mount's scanned files from the shared cache when a fresh entry exists,
    /// otherwise performs the budgeted filesystem scan and caches the result. This keeps
    /// repeated folder-tree polls from re-walking the host directory every request.
    /// </summary>
    private HostMountDirectoryScanner.ScanResult GetOrScanMount(HostFolderMount mount)
    {
        var mountKey = mount.Id.ToString("N");
        var cacheKey = HostMountListingCache.ShallowKey(mountKey);
        return HostMountListingCache.GetOrAdd(
            cacheKey,
            () =>
            {
                var scanRoots = new List<HostMountDirectoryScanner.MountRoot>
                {
                    new(mount.LeafName, mount.ContainerSourcePath)
                };

                var scanResult = HostMountDirectoryScanner.Scan(
                    scanRoots,
                    _linkedMountTreeMaxFiles,
                    _linkedMountTreeMaxDepth,
                    _linkedMountTreeScanBudget,
                    _logger);
                _logger.LogInformation(
                    "Project host mount shallow_scan for mount {MountId} (files={FileCount}, dirs={DirCount}, truncated={Truncated})",
                    mount.Id,
                    scanResult.Files.Count,
                    scanResult.Directories.Count,
                    scanResult.WasTruncated);
                return scanResult;
            },
            _projectMountTreeCacheTtl);
    }

    private FolderTreeDto BuildMountFolderTree(
        HostFolderMount mount,
        IReadOnlyList<HostMountDirectoryScanner.ScannedFile> files,
        IReadOnlyList<HostMountDirectoryScanner.ScannedDirectory> directories)
    {
        var folderChildren = new Dictionary<string, List<FolderTreeDto>>(StringComparer.OrdinalIgnoreCase);
        var fileChildren = new Dictionary<string, List<ContentFileDetailsDto>>(StringComparer.OrdinalIgnoreCase);

        folderChildren[mount.LeafName] = [];
        fileChildren[mount.LeafName] = [];

        foreach (var directory in directories)
        {
            EnsureFolderPath(directory.RelativePath, mount.LeafName, folderChildren, fileChildren);
        }

        foreach (var file in files)
        {
            var dirPath = Path.GetDirectoryName(file.RelativePath)?.Replace("\\", "/") ?? mount.LeafName;
            if (string.IsNullOrEmpty(dirPath)) dirPath = mount.LeafName;

            EnsureFolderPath(dirPath, mount.LeafName, folderChildren, fileChildren);

            if (!fileChildren.ContainsKey(dirPath))
                fileChildren[dirPath] = [];

            _contentTypeProvider.TryGetContentType(file.FileName, out var ct);

            fileChildren[dirPath].Add(new ContentFileDetailsDto(
                Id: CreateMountVirtualFileId(mount.Id, file.RelativePath),
                FileName: file.FileName,
                Path: "",
                RelativePath: file.RelativePath,
                ContentType: ct ?? "application/octet-stream",
                Index: false,
                DocumentId: "",
                Created: file.LastModifiedUtc,
                FileSize: file.FileSize,
                FolderId: null,
                FolderPath: dirPath,
                LatestVersion: 0,
                IsSnapshot: false,
                HasMarkdownShadow: false,
                MarkdownStatus: null,
                MarkdownProcessedAt: null));
        }

        return BuildMountSubTree(mount.LeafName, mount, folderChildren, fileChildren);
    }

    private static void EnsureFolderPath(
        string folderPath,
        string mountRoot,
        Dictionary<string, List<FolderTreeDto>> folderChildren,
        Dictionary<string, List<ContentFileDetailsDto>> fileChildren)
    {
        if (folderChildren.ContainsKey(folderPath)) return;

        var parts = folderPath.Replace("\\", "/").Split('/');
        var current = "";
        for (var i = 0; i < parts.Length; i++)
        {
            var parent = current;
            current = i == 0 ? parts[0] : $"{current}/{parts[i]}";

            if (!folderChildren.ContainsKey(current))
            {
                folderChildren[current] = [];
                fileChildren[current] = [];

                if (!string.IsNullOrEmpty(parent) && folderChildren.ContainsKey(parent))
                {
                    // Will be built later in BuildMountSubTree
                }
            }
        }
    }

    private static FolderTreeDto BuildMountSubTree(
        string folderPath,
        HostFolderMount mount,
        Dictionary<string, List<FolderTreeDto>> folderChildren,
        Dictionary<string, List<ContentFileDetailsDto>> fileChildren)
    {
        var name = Path.GetFileName(folderPath);
        if (string.IsNullOrEmpty(name)) name = folderPath;

        var childFolderPaths = folderChildren.Keys
            .Where(k => !string.Equals(k, folderPath, StringComparison.OrdinalIgnoreCase)
                && IsDirectChild(folderPath, k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subFolders = childFolderPaths
            .Select(p => BuildMountSubTree(p, mount, folderChildren, fileChildren))
            .ToList();

        var files = fileChildren.TryGetValue(folderPath, out var f) ? f : [];

        var isRoot = string.Equals(folderPath, mount.LeafName, StringComparison.OrdinalIgnoreCase);

        return new FolderTreeDto(
            Id: CreateMountVirtualFolderId(mount.Id, folderPath),
            Name: name,
            RelativePath: folderPath,
            SubFolders: subFolders,
            Files: files,
            IsHostMount: isRoot,
            MountId: isRoot ? mount.Id : null,
            MountStatus: isRoot ? mount.Status : null,
            IsLinked: !isRoot);
    }

    private static Guid CreateMountVirtualFolderId(Guid mountId, string folderPath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"mount:{mountId:N}:{folderPath.ToLowerInvariant()}"));
        var guidBytes = new byte[16];
        Buffer.BlockCopy(digest, 0, guidBytes, 0, guidBytes.Length);
        return new Guid(guidBytes);
    }

    private static Guid CreateMountVirtualFileId(Guid mountId, string relativePath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"mountfile:{mountId:N}:{relativePath.Replace("\\", "/").ToLowerInvariant()}"));
        var guidBytes = new byte[16];
        Buffer.BlockCopy(digest, 0, guidBytes, 0, guidBytes.Length);
        return new Guid(guidBytes);
    }

    private static bool IsDirectChild(string parent, string candidate)
    {
        if (!candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        var remainder = candidate[(parent.Length + 1)..];
        return !remainder.Contains('/');
    }

    private static int ReadPositiveInt(string? rawValue, int fallback, int min, int max)
    {
        if (!int.TryParse(rawValue, out var parsed))
            return fallback;
        if (parsed < min) return min;
        if (parsed > max) return max;
        return parsed;
    }
}
