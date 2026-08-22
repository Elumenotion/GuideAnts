using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.StaticFiles;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuideAntsApi.Services.Components.Sync;

namespace GuideAntsApi.Services.Components;

public class NotebookFileService : INotebookFileService
{
    private const int DefaultLinkedMountTreeMaxFiles = 5000;
    private const int DefaultLinkedMountTreeMaxDepth = 3;
    private const int DefaultLinkedMountTreeScanBudgetMs = 2500;
    private const int DefaultLinkedMountTreeCacheSeconds = 120;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _storagePath;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();
    private readonly INotebookFileSyncService _syncService;
    private readonly ILogger<NotebookFileService> _logger;
    private readonly IFileLineageService _lineageService;
    private readonly IContentFileService _contentFileService;
    private readonly IMarkdownExtractionService _markdownExtractionService;
    private readonly IStoragePathResolver _pathResolver;
    private readonly int _linkedMountTreeMaxFiles;
    private readonly int _linkedMountTreeMaxDepth;
    private readonly TimeSpan _linkedMountTreeScanBudget;
    private readonly TimeSpan _linkedMountTreeCacheTtl;

    public NotebookFileService(IServiceScopeFactory scopeFactory, IConfiguration configuration, INotebookFileSyncService syncService, ILogger<NotebookFileService> logger, IFileLineageService lineageService, IContentFileService contentFileService, IMarkdownExtractionService markdownExtractionService, IStoragePathResolver pathResolver)
    {
        _scopeFactory = scopeFactory;
        _storagePath = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        _syncService = syncService;
        _logger = logger;
        _lineageService = lineageService;
        _contentFileService = contentFileService;
        _markdownExtractionService = markdownExtractionService;
        _pathResolver = pathResolver;
        _linkedMountTreeMaxFiles = ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeMaxFiles"],
            DefaultLinkedMountTreeMaxFiles,
            min: 1,
            max: 100000);
        _linkedMountTreeMaxDepth = ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeMaxDepth"],
            DefaultLinkedMountTreeMaxDepth,
            min: 1,
            max: 32);
        _linkedMountTreeScanBudget = TimeSpan.FromMilliseconds(ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeScanBudgetMs"],
            DefaultLinkedMountTreeScanBudgetMs,
            min: 250,
            max: 30000));
        _linkedMountTreeCacheTtl = TimeSpan.FromSeconds(ReadPositiveInt(
            configuration["FileStorage:LinkedMountTreeCacheSeconds"],
            DefaultLinkedMountTreeCacheSeconds,
            min: 1,
            max: 3600));
    }

    // Backward-compatible overload used by tests.
    public NotebookFileService(IServiceScopeFactory scopeFactory, IConfiguration configuration, INotebookFileSyncService syncService, ILogger<NotebookFileService> logger, IFileLineageService lineageService, IContentFileService contentFileService, IMarkdownExtractionService markdownExtractionService)
        : this(
            scopeFactory,
            configuration,
            syncService,
            logger,
            lineageService,
            contentFileService,
            markdownExtractionService,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")))
    { }
    
    // Backward-compatible overload (tests may still pass IServiceProvider). Forward to primary ctor.
    public NotebookFileService(IServiceScopeFactory scopeFactory, IConfiguration configuration, INotebookFileSyncService syncService, ILogger<NotebookFileService> logger, IFileLineageService lineageService, IContentFileService contentFileService, IMarkdownExtractionService markdownExtractionService, IStoragePathResolver pathResolver, IServiceProvider _)
        : this(scopeFactory, configuration, syncService, logger, lineageService, contentFileService, markdownExtractionService, pathResolver)
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

    public async Task<IEnumerable<NotebookFileDto>> ListFilesAsync(Guid projectId, Guid notebookId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Return current state from database without waiting for sync
        var files = await context.NotebookFiles
            .Where(f => f.NotebookId == notebookId)
            .ToListAsync();

        var hiddenProjectedPaths = GetHiddenOutputProjectionPaths(projectId, notebookId, files);

        // Filter out temporary script files, __pycache__ folders, and Resources files (defense-in-depth)
        return files
            .Where(f => !hiddenProjectedPaths.Contains(NormalizeRelativePath(f.RelativePath)))
            .Where(f => !IsTemporaryScriptFile(Path.GetFileName(f.RelativePath)))
            .Where(f => !IsInPycacheFolder(f.RelativePath))
            .Where(f => !IsInResourcesFolder(f.RelativePath))
            .Where(f => !IsInGuideantsFolder(f.RelativePath))
            .Select(f => new NotebookFileDto(f.Id, Path.GetFileName(f.RelativePath), f.RelativePath, f.FileSize, f.LastModifiedUtc, f.FileHash, f.OriginContentFileVersionId, false, false)); // Index removed
    }

    public async Task<NotebookFolderTreeDto?> GetFolderTreeAsync(Guid projectId, Guid notebookId)
    {

using var scope2 = CreateDbScope();
        var context = GetDbContext(scope2);
        
        // Get files from database - fast indexed query
        var files = await context.NotebookFiles
            .Where(f => f.NotebookId == notebookId)
            .ToListAsync();

        var hiddenProjectedPaths = GetHiddenOutputProjectionPaths(projectId, notebookId, files);

        // Filter out temporary script files, __pycache__ folders, and Resources files
        var fileDtos = files
            .Where(f => !hiddenProjectedPaths.Contains(NormalizeRelativePath(f.RelativePath)))
            .Where(f => !IsTemporaryScriptFile(Path.GetFileName(f.RelativePath)))
            .Where(f => !IsInPycacheFolder(f.RelativePath))
            .Where(f => !IsInResourcesFolder(f.RelativePath))
            .Where(f => !IsInGuideantsFolder(f.RelativePath))
            .Select(f => new NotebookFileDto(f.Id, Path.GetFileName(f.RelativePath), f.RelativePath, f.FileSize, f.LastModifiedUtc, f.FileHash, f.OriginContentFileVersionId, false, false))
            .ToList();

        var (linkedFileDtos, linkedDirectories) = await BuildLinkedMountOverlayAsync(context, notebookId);
        if (linkedFileDtos.Count > 0)
        {
            var existingPaths = new HashSet<string>(
                fileDtos.Select(f => f.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (var linked in linkedFileDtos)
            {
                if (!existingPaths.Contains(linked.RelativePath))
                {
                    fileDtos.Add(linked);
                }
            }
        }
        
        return BuildNotebookFolderTree(fileDtos, linkedDirectories);
    }

    public async Task<HostMountListingDto?> ListHostMountLevelAsync(
        Guid projectId,
        Guid notebookId,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        var linkedMountRoots = await GetLinkedMountRootsAsync(context, notebookId);
        if (linkedMountRoots.Count == 0)
        {
            return null;
        }

        var normalizedPath = NormalizeRelativePath(relativePath);
        var mount = linkedMountRoots.FirstOrDefault(root =>
            normalizedPath.Equals(root.LinkRelativePath, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(root.LinkRelativePath + "/", StringComparison.OrdinalIgnoreCase));
        if (mount == null)
        {
            return null;
        }

        var withinMount = normalizedPath.Equals(mount.LinkRelativePath, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalizedPath[(mount.LinkRelativePath.Length + 1)..];

        var mountKey = mount.MountId.ToString("N");
        var cacheKey = HostMountListingCache.LevelKey(mountKey, normalizedPath);
        HostMountDirectoryScanner.ScanResult scanResult;
        if (HostMountListingCache.TryGet(cacheKey, out var cached))
        {
            _logger.LogDebug(
                "Host mount listing cache_hit for notebook {NotebookId} path {Path}",
                notebookId,
                normalizedPath);
            scanResult = cached;
        }
        else
        {
            scanResult = HostMountDirectoryScanner.ListLevel(
                mount.LinkPhysicalPath,
                mount.LinkRelativePath,
                withinMount,
                _linkedMountTreeMaxFiles,
                _linkedMountTreeScanBudget,
                _logger);
            HostMountListingCache.Set(cacheKey, scanResult, _linkedMountTreeCacheTtl);
            _logger.LogInformation(
                "Host mount lazy_list for notebook {NotebookId} path {Path} (files={FileCount}, dirs={DirCount}, truncated={Truncated})",
                notebookId,
                normalizedPath,
                scanResult.Files.Count,
                scanResult.Directories.Count,
                scanResult.WasTruncated);
        }

        var folders = scanResult.Directories
            .Where(d => !IsInResourcesFolder(d.RelativePath) && !IsInGuideantsFolder(d.RelativePath))
            .Select(d => new HostMountListingFolderDto(d.Name, d.RelativePath))
            .ToList();

        var listingFiles = scanResult.Files
            .Where(f => !IsInResourcesFolder(f.RelativePath) && !IsInGuideantsFolder(f.RelativePath))
            .Select(f => new HostMountListingFileDto(
                Id: CreateLinkedVirtualFileId(f.RelativePath),
                FileName: f.FileName,
                RelativePath: f.RelativePath,
                FileSize: f.FileSize,
                LastModifiedUtc: f.LastModifiedUtc,
                FileHash: $"{f.FileSize:x}-{f.LastModifiedUtc.Ticks:x}",
                IsLinked: true))
            .ToList();

        return new HostMountListingDto(normalizedPath, folders, listingFiles, scanResult.WasTruncated);
    }

    private static NotebookFolderTreeDto BuildNotebookFolderTree(
        List<NotebookFileDto> files,
        IReadOnlyCollection<string>? linkedDirectories = null)
    {
        // Build folder tree from file paths plus explicit directory stubs (host mounts).
        // Empty non-mount folders are still managed client-side until they contain files.
        
        var folderStructure = new Dictionary<string, NotebookFolderTreeDto>(StringComparer.OrdinalIgnoreCase);
        var rootFiles = new List<NotebookFileDto>();

        void EnsureFolderPath(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var pathParts = folderPath.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = "";
            foreach (var folderName in pathParts)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? folderName : $"{currentPath}/{folderName}";
                if (!folderStructure.ContainsKey(currentPath))
                {
                    folderStructure[currentPath] = new NotebookFolderTreeDto(
                        folderName,
                        currentPath,
                        new List<NotebookFolderTreeDto>(),
                        new List<NotebookFileDto>()
                    );
                }
            }
        }

        foreach (var file in files)
        {
            if (!file.RelativePath.Contains('/'))
            {
                rootFiles.Add(file);
            }
        }

        foreach (var file in files.Where(f => f.RelativePath.Contains('/')))
        {
            var pathParts = file.RelativePath.Split('/');
            var currentPath = "";

            for (int i = 0; i < pathParts.Length - 1; i++)
            {
                var folderName = pathParts[i];
                currentPath = string.IsNullOrEmpty(currentPath) ? folderName : $"{currentPath}/{folderName}";

                if (!folderStructure.ContainsKey(currentPath))
                {
                    folderStructure[currentPath] = new NotebookFolderTreeDto(
                        folderName,
                        currentPath,
                        new List<NotebookFolderTreeDto>(),
                        new List<NotebookFileDto>()
                    );
                }

                if (i == pathParts.Length - 2)
                {
                    folderStructure[currentPath].Files.Add(file);
                }
            }
        }

        if (linkedDirectories != null)
        {
            foreach (var directory in linkedDirectories)
            {
                EnsureFolderPath(directory);
            }
        }

        var rootFolders = new List<NotebookFolderTreeDto>();
        var processedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in folderStructure.OrderBy(f => f.Key))
        {
            var folderPath = kvp.Key;
            var folder = kvp.Value;

            if (processedFolders.Contains(folderPath)) continue;

            var pathParts = folderPath.Split('/');
            if (pathParts.Length == 1)
            {
                AttachSubFolders(folder, folderStructure, processedFolders);
                rootFolders.Add(folder);
            }
        }

        return new NotebookFolderTreeDto(
            "Root",
            "",
            rootFolders,
            rootFiles
        );
    }

    private static void AttachSubFolders(NotebookFolderTreeDto parentFolder, Dictionary<string, NotebookFolderTreeDto> allFolders, HashSet<string> processedFolders)
    {
        processedFolders.Add(parentFolder.RelativePath);

        var childFolders = allFolders.Where(f => 
            f.Key.StartsWith(parentFolder.RelativePath + "/") &&
            f.Key.Substring(parentFolder.RelativePath.Length + 1).Split('/').Length == 1
        ).ToList();

        foreach (var childKvp in childFolders)
        {
            var childFolder = childKvp.Value;
            if (!processedFolders.Contains(childFolder.RelativePath))
            {
                AttachSubFolders(childFolder, allFolders, processedFolders);
                parentFolder.SubFolders.Add(childFolder);
            }
        }
    }

    public async Task<(Stream Stream, string ContentType, string FileName)?> GetFileAsync(Guid projectId, Guid notebookId, string relativePath)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var (file, resolvedPath) = await FindNotebookFileByRelativePathAsync(context, notebookId, relativePath);
        if (file == null)
        {
            var linkedPath = await ResolveLinkedMountPhysicalPathAsync(context, projectId, notebookId, relativePath);
            if (linkedPath == null)
            {
                return null;
            }

            var linkedContentType = _contentTypeProvider.TryGetContentType(linkedPath.Value.ResolvedRelativePath, out var linkedCt)
                ? linkedCt
                : "application/octet-stream";
            var linkedStream = new FileStream(linkedPath.Value.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return (linkedStream, linkedContentType, Path.GetFileName(linkedPath.Value.ResolvedRelativePath));
        }

        if (!TryResolveNotebookPath(projectId, notebookId, resolvedPath, out var physicalPath))
        {
            return null;
        }

        if (!File.Exists(physicalPath)) return null;

        var contentType = _contentTypeProvider.TryGetContentType(file.RelativePath, out var ct) ? ct : "application/octet-stream";
        var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (stream, contentType, Path.GetFileName(file.RelativePath));
    }

    public async Task<(Stream stream, string contentType)> GetFileContentStreamAsync(Guid projectId, Guid notebookId, string relativePath)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var (file, normalizedPath) = await FindNotebookFileByRelativePathAsync(context, notebookId, relativePath);

        if (file == null)
        {
            var linkedPath = await ResolveLinkedMountPhysicalPathAsync(context, projectId, notebookId, relativePath);
            if (linkedPath == null)
            {
                throw new FileNotFoundException("File not found.", relativePath);
            }

            var linkedContentType = _contentTypeProvider.TryGetContentType(linkedPath.Value.ResolvedRelativePath, out var linkedCt)
                ? linkedCt
                : "application/octet-stream";
            var linkedStream = new FileStream(linkedPath.Value.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return (linkedStream, linkedContentType);
        }

        if (!TryResolveNotebookPath(projectId, notebookId, normalizedPath, out var physicalPath))
        {
            throw new FileNotFoundException("File not found on disk.", normalizedPath);
        }

        if (!File.Exists(physicalPath))
        {
            throw new FileNotFoundException("File not found on disk.", physicalPath);
        }

        var contentType = _contentTypeProvider.TryGetContentType(file.RelativePath, out var ct) ? ct : "application/octet-stream";
        var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (stream, contentType);
    }

    private async Task<(string ResolvedRelativePath, string PhysicalPath)?> ResolveLinkedMountPhysicalPathAsync(
        ApplicationDbContext context,
        Guid projectId,
        Guid notebookId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = relativePath.Replace("\\", "/").TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var linkedMountRoots = await GetLinkedMountRootsAsync(context, notebookId, cancellationToken);
        if (linkedMountRoots.Count == 0)
        {
            return null;
        }

        foreach (var candidateRaw in EnumerateAlternativeLinkedCandidates(normalizedPath))
        {
            var candidate = candidateRaw.Replace("\\", "/").TrimStart('/');
            if (!IsUnderLinkedMountRoot(candidate, linkedMountRoots))
            {
                continue;
            }

            if (!TryResolveNotebookPath(projectId, notebookId, candidate, out var candidatePhysicalPath))
            {
                continue;
            }

            if (!File.Exists(candidatePhysicalPath))
            {
                continue;
            }

            return (candidate, candidatePhysicalPath);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAlternativeLinkedCandidates(string normalizedPath)
    {
        yield return normalizedPath;
        foreach (var alternative in NotebookPathResolver.GetAlternativePaths(normalizedPath))
        {
            if (string.Equals(alternative, normalizedPath, StringComparison.Ordinal))
            {
                continue;
            }

            yield return alternative;
        }
    }

    private static bool IsUnderLinkedMountRoot(string candidateRelativePath, IReadOnlyCollection<LinkedMountRoot> linkedMountRoots)
    {
        var normalizedCandidate = candidateRelativePath.Replace("\\", "/").Trim('/');
        if (string.IsNullOrEmpty(normalizedCandidate))
        {
            return false;
        }

        foreach (var root in linkedMountRoots)
        {
            if (normalizedCandidate.Equals(root.LinkRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedCandidate.StartsWith(root.LinkRelativePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<List<LinkedMountRoot>> GetLinkedMountRootsAsync(
        ApplicationDbContext context,
        Guid notebookId,
        CancellationToken cancellationToken = default)
    {
        var roots = await context.HostFolderMountLinks
            .AsNoTracking()
            .Where(link => link.NotebookId == notebookId && link.Status == HostFolderMountLinkStatus.Linked)
            .Join(
                context.HostFolderMounts.AsNoTracking(),
                link => link.HostFolderMountId,
                mount => mount.Id,
                (link, mount) => new { Link = link, Mount = mount })
            .Where(row => row.Mount.Status != HostFolderMountStatus.Removed)
            .Select(row => new LinkedMountRoot(
                row.Mount.Id,
                row.Link.LinkRelativePath.Replace("\\", "/").Trim('/'),
                row.Link.LinkPhysicalPath))
            .ToListAsync(cancellationToken);

        var deduped = new Dictionary<string, LinkedMountRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.LinkRelativePath))
            {
                continue;
            }

            if (!deduped.ContainsKey(root.LinkRelativePath))
            {
                deduped[root.LinkRelativePath] = root;
            }
        }

        return deduped.Values.ToList();
    }

    private sealed record LinkedMountRoot(Guid MountId, string LinkRelativePath, string LinkPhysicalPath);

    private async Task<(List<NotebookFileDto> Files, List<string> Directories)> BuildLinkedMountOverlayAsync(
        ApplicationDbContext context,
        Guid notebookId,
        CancellationToken cancellationToken = default)
    {
        var linkedMountRoots = await GetLinkedMountRootsAsync(context, notebookId, cancellationToken);
        if (linkedMountRoots.Count == 0)
        {
            return ([], []);
        }

        var allFiles = new List<NotebookFileDto>();
        var allDirectories = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in linkedMountRoots)
        {
            var mountKey = root.MountId.ToString("N");
            var cacheKey = HostMountListingCache.ShallowKey(mountKey);
            HostMountDirectoryScanner.ScanResult scanResult;
            if (HostMountListingCache.TryGet(cacheKey, out var cached))
            {
                _logger.LogDebug(
                    "Host mount shallow cache_hit for notebook {NotebookId} mount {MountId}",
                    notebookId,
                    root.MountId);
                scanResult = cached;
            }
            else
            {
                scanResult = HostMountDirectoryScanner.Scan(
                    [new HostMountDirectoryScanner.MountRoot(root.LinkRelativePath, root.LinkPhysicalPath)],
                    _linkedMountTreeMaxFiles,
                    _linkedMountTreeMaxDepth,
                    _linkedMountTreeScanBudget,
                    _logger);
                HostMountListingCache.Set(cacheKey, scanResult, _linkedMountTreeCacheTtl);
                _logger.LogInformation(
                    "Host mount shallow_scan for notebook {NotebookId} mount {MountId} (files={FileCount}, dirs={DirCount}, truncated={Truncated})",
                    notebookId,
                    root.MountId,
                    scanResult.Files.Count,
                    scanResult.Directories.Count,
                    scanResult.WasTruncated);

                if (scanResult.WasTruncated)
                {
                    _logger.LogWarning(
                        "Linked mount tree enumeration was truncated for notebook {NotebookId} mount {MountId} (maxFiles={MaxFiles}, maxDepth={MaxDepth}, scanBudgetMs={ScanBudgetMs}).",
                        notebookId,
                        root.MountId,
                        _linkedMountTreeMaxFiles,
                        _linkedMountTreeMaxDepth,
                        (int)_linkedMountTreeScanBudget.TotalMilliseconds);
                }
            }

            if (seenDirs.Add(root.LinkRelativePath))
            {
                allDirectories.Add(root.LinkRelativePath);
            }

            foreach (var directory in scanResult.Directories)
            {
                if (IsInResourcesFolder(directory.RelativePath) || IsInGuideantsFolder(directory.RelativePath))
                {
                    continue;
                }

                if (seenDirs.Add(directory.RelativePath))
                {
                    allDirectories.Add(directory.RelativePath);
                }
            }

            foreach (var file in scanResult.Files)
            {
                if (IsInResourcesFolder(file.RelativePath) || IsInGuideantsFolder(file.RelativePath))
                {
                    continue;
                }

                if (!seenFiles.Add(file.RelativePath))
                {
                    continue;
                }

                allFiles.Add(new NotebookFileDto(
                    Id: CreateLinkedVirtualFileId(file.RelativePath),
                    FileName: file.FileName,
                    RelativePath: file.RelativePath,
                    FileSize: file.FileSize,
                    LastModifiedUtc: file.LastModifiedUtc,
                    FileHash: $"{file.FileSize:x}-{file.LastModifiedUtc.Ticks:x}",
                    OriginContentFileVersionId: null,
                    Index: false,
                    IsIndexed: false,
                    IsLinked: true));
            }
        }

        return (allFiles, allDirectories);
    }

    private static Guid CreateLinkedVirtualFileId(string relativePath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("linked:" + relativePath.ToLowerInvariant()));
        var guidBytes = new byte[16];
        Buffer.BlockCopy(digest, 0, guidBytes, 0, guidBytes.Length);
        return new Guid(guidBytes);
    }

    private static int ReadPositiveInt(string? rawValue, int fallback, int min, int max)
    {
        if (!int.TryParse(rawValue, out var parsed))
        {
            return fallback;
        }

        if (parsed < min)
        {
            return min;
        }

        if (parsed > max)
        {
            return max;
        }

        return parsed;
    }

    private async Task InvalidateMountCachesForNotebookPathAsync(Guid notebookId, string relativePath)
    {
        try
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            var roots = await GetLinkedMountRootsAsync(context, notebookId);
            var normalized = NormalizeRelativePath(relativePath);
            foreach (var root in roots)
            {
                if (normalized.Equals(root.LinkRelativePath, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(root.LinkRelativePath + "/", StringComparison.OrdinalIgnoreCase)
                    || root.LinkRelativePath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Length == 0)
                {
                    HostMountListingCache.InvalidateMount(root.MountId.ToString("N"));
                    _logger.LogInformation(
                        "Host mount cache_invalidated for mount {MountId} due to path {Path}",
                        root.MountId,
                        normalized);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate host mount listing cache for notebook {NotebookId}", notebookId);
        }
    }

    private async Task<(NotebookFile? file, string normalizedPath)> FindNotebookFileByRelativePathAsync(
        ApplicationDbContext context,
        Guid notebookId,
        string relativePath)
    {
        var normalizedPath = relativePath.Replace("\\", "/");

        var file = await context.NotebookFiles.FirstOrDefaultAsync(
            f => f.NotebookId == notebookId && f.RelativePath == normalizedPath);
        if (file != null)
        {
            return (file, normalizedPath);
        }

        foreach (var altPath in NotebookPathResolver.GetAlternativePaths(normalizedPath))
        {
            file = await context.NotebookFiles.FirstOrDefaultAsync(
                f => f.NotebookId == notebookId && f.RelativePath == altPath);
            if (file != null)
            {
                _logger.LogInformation(
                    "Resolved file path from '{OriginalPath}' to '{ResolvedPath}'",
                    LogValueSanitizer.Sanitize(normalizedPath),
                    LogValueSanitizer.Sanitize(altPath));
                return (file, altPath);
            }
        }

        if (ShouldTryCwdSuffixMatch(normalizedPath))
        {
            file = await context.NotebookFiles
                .Where(f => f.NotebookId == notebookId)
                .Where(f => f.RelativePath == normalizedPath || f.RelativePath.EndsWith("/" + normalizedPath))
                .OrderByDescending(f => f.LastModifiedUtc)
                .FirstOrDefaultAsync();
            if (file != null)
            {
                _logger.LogInformation(
                    "Resolved CWD-relative path '{OriginalPath}' to '{ResolvedPath}'",
                    LogValueSanitizer.Sanitize(normalizedPath),
                    LogValueSanitizer.Sanitize(file.RelativePath));
                return (file, file.RelativePath);
            }
        }

        return (null, normalizedPath);
    }

    /// <summary>
    /// Whether to try suffix matching for CWD-relative paths (e.g. "duck.png" -> "Runs/{runId}/duck.png").
    /// </summary>
    private static bool ShouldTryCwdSuffixMatch(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith("../", StringComparison.Ordinal)
        && !path.StartsWith("Runs/", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("Output/", StringComparison.OrdinalIgnoreCase);

    public async Task<(Stream Stream, string ContentType, string FileName)?> GetFileContentStreamAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
    {
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var nf = await context.NotebookFiles
            .Include(f => f.Notebook)
            .FirstOrDefaultAsync(f => f.Id == notebookFileId, cancellationToken);
        if (nf == null) return null;

        var notebookRoot = GetNotebookRootPath(nf.Notebook.ProjectId, nf.NotebookId);
        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, nf.RelativePath, out var physicalPath))
        {
            return null;
        }

        if (!File.Exists(physicalPath)) return null;

        var fileName = Path.GetFileName(nf.RelativePath);
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(fileName, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (stream, contentType, fileName);
    }

    private string GetNotebookRootPath(Guid projectId, Guid notebookId)
    {
        return _pathResolver.GetNotebookRootPath(projectId, notebookId);
    }

    private bool TryResolveNotebookPath(Guid projectId, Guid notebookId, string? relativePath, out string fullPath)
    {
        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        return NotebookStoragePath.TryResolveUnderRoot(notebookRoot, relativePath, out fullPath);
    }

    public async Task<NotebookFileDto?> CopyFromProjectAsync(Guid projectId, Guid notebookId, Guid contentFileId, int? versionNumber, string? targetRelativePath)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var contentFile = await context.ContentFiles.FirstOrDefaultAsync(f => f.Id == contentFileId && f.ProjectId == projectId);
        if (contentFile == null) return null;
        var verNum = versionNumber ?? contentFile.LatestVersion;
        var version = await context.ContentFileVersions.FirstOrDefaultAsync(v => v.ContentFileId == contentFileId && v.VersionNumber == verNum);
        if (version == null) return null;

        // Resolve the source file path - prefer StoragePath (content-addressable) over deprecated Path
        var sourcePath = !string.IsNullOrEmpty(version.StoragePath) ? version.StoragePath : version.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            _logger.LogError("No valid storage path found for ContentFileVersion {VersionId}", version.Id);
            return null;
        }

        // Ensure the source file exists
        if (!File.Exists(sourcePath))
        {
            _logger.LogError("Source file not found at path: {SourcePath}", LogValueSanitizer.Sanitize(sourcePath));
            return null;
        }

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        Directory.CreateDirectory(notebookRoot);
        var relativePath = string.IsNullOrWhiteSpace(targetRelativePath) ? version.FileName : targetRelativePath;
        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, relativePath, out var destPath))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(sourcePath, destPath, overwrite: true);

        // --- Prepare markdown shadow before creating NotebookFile ---
        NotebookFileMarkdownShadow? copiedShadow = null;
        try
        {
            // Check if the source ContentFileVersion has a completed markdown shadow
            var sourceMarkdownShadow = await _markdownExtractionService.GetMarkdownShadowAsync(version.Id);
            
            if (sourceMarkdownShadow?.Status == MarkdownExtractionStatus.Completed && 
                !string.IsNullOrEmpty(sourceMarkdownShadow.StoragePath) && 
                File.Exists(sourceMarkdownShadow.StoragePath))
            {
                // Create the shadow record but don't save it yet (we need the NotebookFile ID first)
                copiedShadow = new NotebookFileMarkdownShadow
                {
                    ContentHash = sourceMarkdownShadow.ContentHash,
                    StoragePath = sourceMarkdownShadow.StoragePath, // Reuse the same markdown file
                    FileSize = sourceMarkdownShadow.FileSize,
                    Status = MarkdownExtractionStatus.Completed,
                    ProcessedAt = DateTime.UtcNow
                };
                
                _logger.LogInformation("Prepared completed markdown shadow copy from ContentFileVersion {SourceVersionId}", version.Id);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the copy operation if markdown shadow preparation fails
            _logger.LogError(ex, "Failed to prepare markdown shadow for copied file {RelativePath}", LogValueSanitizer.Sanitize(relativePath));
        }

        // --- Manually create NotebookFile record instead of relying on sync ---
        var normalizedRelPath = relativePath.Replace("\\", "/");
        var fileInfo = new FileInfo(destPath);
        var fileSize = fileInfo.Length;
        var lastModifiedUtc = fileInfo.LastWriteTimeUtc;
        var hash = ComputeSha256(destPath);

        // Check if NotebookFile already exists (handle overwrite case)
        var nf = await context.NotebookFiles.FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == normalizedRelPath);
        if (nf == null)
        {
            // Create new NotebookFile record
            nf = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = normalizedRelPath,
                FileSize = fileSize,
                LastModifiedUtc = lastModifiedUtc,
                FileHash = hash,
                // Index removed - handled by shadow indexer
                OriginContentFileVersionId = version.Id
            };
            nf.GenerateDocumentId(notebookId);
            context.NotebookFiles.Add(nf);
        }
        else
        {
            // Update existing NotebookFile record
            nf.FileSize = fileSize;
            nf.LastModifiedUtc = lastModifiedUtc;
            nf.FileHash = hash;
            nf.OriginContentFileVersionId = version.Id;
        }

        await context.SaveChangesAsync();

        // --- Handle markdown shadow creation ---
        try
        {
            if (copiedShadow != null)
            {
                // Check if a shadow already exists for this NotebookFile
                var existingShadow = await context.NotebookFileMarkdownShadows
                    .FirstOrDefaultAsync(s => s.OriginalNotebookFileId == nf.Id);

                if (existingShadow != null)
                {
                    // Update existing shadow with the copied data
                    existingShadow.ContentHash = copiedShadow.ContentHash;
                    existingShadow.StoragePath = copiedShadow.StoragePath;
                    existingShadow.FileSize = copiedShadow.FileSize;
                    existingShadow.Status = copiedShadow.Status;
                    existingShadow.ProcessedAt = copiedShadow.ProcessedAt;
                    
                    _logger.LogInformation("Updated existing markdown shadow for NotebookFile {NotebookFileId} with copied data from ContentFileVersion {SourceVersionId}", 
                        nf.Id, version.Id);
                }
                else
                {
                    // Create new shadow with the copied data
                    copiedShadow.OriginalNotebookFileId = nf.Id;
                    context.NotebookFileMarkdownShadows.Add(copiedShadow);
                    
                    _logger.LogInformation("Created markdown shadow for NotebookFile {NotebookFileId} with copied data from ContentFileVersion {SourceVersionId}", 
                        nf.Id, version.Id);
                }
                
                await context.SaveChangesAsync();
            }
            else
            {
                // No completed markdown shadow exists, create a pending one for extraction
                await _markdownExtractionService.CreateNotebookMarkdownShadowAsync(nf.Id);
                _logger.LogInformation("Created pending markdown shadow for copied NotebookFile {NotebookFileId}", nf.Id);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the copy operation if markdown shadow creation fails
            _logger.LogError(ex, "Failed to create markdown shadow for copied file {RelativePath}", LogValueSanitizer.Sanitize(nf.RelativePath));
        }

        // Record lineage: project side (CopiedToNotebook)
        // Cannot include notebookId due to CK_FileLineageEvent_NotebookId constraint
        await _lineageService.RecordAsync(
            FileKind.Project,
            projectId,
            contentFile.Id,
            verNum,
            FileLineageAction.CopiedToNotebook,
            null, // Must be null for FileKind.Project
            version.StoragePath ?? string.Empty);

        // Record lineage: notebook side (Created)
        await _lineageService.RecordAsync(
            FileKind.Notebook,
            projectId,
            nf.Id,
            null,
            FileLineageAction.Created,
            notebookId,
            destPath);

        // Kernel Memory removed - notebook tag updates no longer needed

        // NOTE: Fire-and-forget sync removed to prevent race conditions when copying multiple files in parallel.
        // The caller (e.g., frontend after Promise.all) should trigger a sync if needed.

        return new NotebookFileDto(nf.Id, Path.GetFileName(nf.RelativePath), nf.RelativePath, nf.FileSize, nf.LastModifiedUtc, nf.FileHash, nf.OriginContentFileVersionId, false, false); // Index removed
    }

    public async Task<ContentFileDetailsDto> PublishToProjectAsync(Guid projectId, Guid notebookId, Guid notebookFileId, Guid? destinationFolderId, bool index)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebookFile = await context.NotebookFiles
            .FirstOrDefaultAsync(nf => nf.Id == notebookFileId && nf.NotebookId == notebookId);

        if (notebookFile == null)
        {
            throw new ArgumentException("Notebook file not found.");
        }

        if (!TryResolveNotebookPath(projectId, notebookId, notebookFile.RelativePath, out var sourcePath))
        {
            throw new FileNotFoundException("The source notebook file was not found on the server.", notebookFile.RelativePath);
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source notebook file was not found on the server.", sourcePath);
        }
        
        // Determine content type
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(notebookFile.RelativePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        // If the notebook file has lineage, find the original project file to create a new version of it.
        // However, we also respect the user's choice to move it to a new folder.
        if (notebookFile.OriginContentFileVersionId.HasValue)
        {
            var originVersion = await context.ContentFileVersions
                .Include(v => v.ContentFile)
                .FirstOrDefaultAsync(v => v.Id == notebookFile.OriginContentFileVersionId.Value);

            if (originVersion != null)
            {
                var originalContentFile = originVersion.ContentFile;
                
                // If the user is NOT moving the file to a new folder, create a new version of the original file.
                if (destinationFolderId == null || destinationFolderId == originalContentFile.FolderId)
                {
                    return await _contentFileService.CreateVersionFromPathAsync(projectId, originalContentFile.Id, sourcePath, notebookFile.Id, index);
                }
            }
        }
        
        // If there's no lineage, or if the user is publishing to a new folder, create a new file.
        // The CreateFileFromPathAsync method internally handles checking for name conflicts and creating a version if a
        // file with the same name exists in the target folder.
        var fileName = Path.GetFileName(notebookFile.RelativePath);
        return await _contentFileService.CreateFileFromPathAsync(projectId, sourcePath, fileName, contentType, destinationFolderId, notebookFile.Id, index);
    }

    public async Task<IEnumerable<NotebookFileDto>> UploadFilesAsync(Guid projectId, Guid notebookId, IFormFileCollection files, string targetRelativePath, bool index = false, bool forceMarkdownExtraction = false)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks.FindAsync(notebookId);
        if (notebook == null || notebook.ProjectId != projectId)
        {
            throw new ArgumentException("Notebook not found.");
        }

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        if (!NotebookStoragePath.TryResolveDirectoryUnderRoot(notebookRoot, targetRelativePath, out var targetDirectory))
        {
            throw new ArgumentException("Invalid target directory path.");
        }

        Directory.CreateDirectory(targetDirectory);
        
        var processedFiles = new List<NotebookFile>();
        var changedExistingFileIds = new HashSet<Guid>();

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            var safeFileName = NotebookStoragePath.SanitizeFileName(file.FileName)
                ?? throw new ArgumentException("Invalid file name.");
            if (!NotebookStoragePath.TryResolveUnderRoot(targetDirectory, safeFileName, out var physicalPath))
            {
                throw new ArgumentException("Invalid file path.");
            }

            var relativePath = Path.GetRelativePath(notebookRoot, physicalPath).Replace("\\", "/");
            var existingFile = await context.NotebookFiles.FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == relativePath);
            var previousHash = existingFile?.FileHash;
            var previousSize = existingFile?.FileSize;

            // Capture the prior DB hash before touching disk. The notebook sync worker can
            // observe disk writes and update FileHash, so comparing after overwrite can race.
            await using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var fileInfo = new FileInfo(physicalPath);
            var fileHash = ComputeSha256(physicalPath);

            if (existingFile != null)
            {
                // Update existing file record
                var contentChanged = !string.Equals(previousHash, fileHash, StringComparison.OrdinalIgnoreCase);
                var shouldExtract = contentChanged || forceMarkdownExtraction;
                _logger.LogInformation(
                    "Updating existing notebook file. projectId={ProjectId} notebookId={NotebookId} fileId={NotebookFileId} relativePath={RelativePath} oldHash={OldHash} newHash={NewHash} oldSize={OldSize} newSize={NewSize} contentChanged={ContentChanged} forceMarkdownExtraction={ForceMarkdownExtraction} shouldExtract={ShouldExtract}",
                    projectId,
                    notebookId,
                    existingFile.Id,
                    LogValueSanitizer.Sanitize(relativePath),
                    LogValueSanitizer.Sanitize(previousHash),
                    LogValueSanitizer.Sanitize(fileHash),
                    previousSize,
                    fileInfo.Length,
                    contentChanged,
                    forceMarkdownExtraction,
                    shouldExtract);
                existingFile.FileSize = fileInfo.Length;
                existingFile.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
                existingFile.FileHash = fileHash;
                // Preserve existing Index and OriginContentFileVersionId values
                processedFiles.Add(existingFile);
                if (shouldExtract)
                {
                    changedExistingFileIds.Add(existingFile.Id);
                }
            }
            else
            {
                // Create new file record
                var newFile = new NotebookFile
                {
                    NotebookId = notebookId,
                    RelativePath = relativePath,
                    FileSize = fileInfo.Length,
                    LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                    FileHash = fileHash,
                    OriginContentFileVersionId = null, // This is a new, native file
                    // Index removed - handled by shadow indexer
                };
                newFile.GenerateDocumentId(notebookId);
                context.NotebookFiles.Add(newFile);
                processedFiles.Add(newFile);
                _logger.LogInformation(
                    "Created new notebook file record. projectId={ProjectId} notebookId={NotebookId} relativePath={RelativePath} hash={Hash} size={Size}",
                    projectId,
                    notebookId,
                    LogValueSanitizer.Sanitize(relativePath),
                    LogValueSanitizer.Sanitize(fileHash),
                    fileInfo.Length);
            }
        }

        await context.SaveChangesAsync();

        // Index flag removed - KM indexing now handled by shadow indexer

        // Record storage usage for uploaded files
        try
        {
            using var usageScope = _scopeFactory.CreateScope();
            var usageRecorder = usageScope.ServiceProvider.GetRequiredService<GuideAnts.Usage.IUsageRecorder>();
            
            foreach (var nf in processedFiles)
            {
                await usageRecorder.RecordAsync(
                    projectId: projectId,
                    notebookId: notebookId,
                    category: GuideAnts.Usage.UsageCategory.StorageUploaded,
                    service: "Storage",
                    operation: "upload",
                    metrics: new GuideAnts.Usage.UsageMetrics(ValueOther: nf.FileSize),
                    notebookFileId: nf.Id,
                    metadataJson: JsonSerializer.Serialize(new { path = nf.RelativePath }));
            }
        }
        catch { /* best-effort */ }

        // Record lineage events for each created or updated file
        foreach (var nf in processedFiles)
        {
            if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, nf.RelativePath, out var lineagePath))
            {
                continue;
            }

            await _lineageService.RecordAsync(
            FileKind.Notebook,
                projectId,
                nf.Id,
                null,
                FileLineageAction.Uploaded, // Using "Uploaded" for both create and update
                notebookId,
                lineagePath);
        }

        // --- Create markdown shadows for uploaded files ---
        foreach (var nf in processedFiles)
        {
            try
            {
                if (changedExistingFileIds.Contains(nf.Id))
                {
                    await _markdownExtractionService.RetryNotebookExtractionAsync(nf.Id);
                    _logger.LogInformation(
                        "Requeued markdown extraction for updated NotebookFile. projectId={ProjectId} notebookId={NotebookId} notebookFileId={NotebookFileId} relativePath={RelativePath}",
                        projectId,
                        notebookId,
                        nf.Id,
                        LogValueSanitizer.Sanitize(nf.RelativePath));
                }
                else
                {
                    await _markdownExtractionService.CreateNotebookMarkdownShadowAsync(nf.Id);
                    _logger.LogInformation(
                        "Ensured markdown shadow for uploaded NotebookFile. projectId={ProjectId} notebookId={NotebookId} notebookFileId={NotebookFileId} relativePath={RelativePath}",
                        projectId,
                        notebookId,
                        nf.Id,
                        LogValueSanitizer.Sanitize(nf.RelativePath));
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail the upload if markdown shadow creation fails
                _logger.LogError(ex, "Failed to create markdown shadow for uploaded file {RelativePath}", LogValueSanitizer.Sanitize(nf.RelativePath));
            }
        }

        // --- Create indexing jobs for directly indexable uploaded files ---
        foreach (var nf in processedFiles)
        {
            try
            {
                var extension = Path.GetExtension(nf.RelativePath);
                if (IsDirectIndexable(extension))
                {
                    using var jobScope = _scopeFactory.CreateScope();
                    var jobQueue = jobScope.ServiceProvider.GetRequiredService<GuideAntsApi.BackgroundJobs.IJobQueueService>();
                    await jobQueue.EnqueueAsync(
                        jobType: nameof(GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob).Replace("Job", string.Empty),
                        payload: new GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob(nf.Id, IsContentFile: false));
                    _logger.LogInformation("Created indexing job for uploaded NotebookFile {NotebookFileId} ({RelativePath})", nf.Id, LogValueSanitizer.Sanitize(nf.RelativePath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create indexing job for uploaded file {RelativePath}", LogValueSanitizer.Sanitize(nf.RelativePath));
            }
        }

        await QueueNotebookSyncBestEffortAsync(notebookId);

        return processedFiles.Select(nf => new NotebookFileDto(nf.Id, Path.GetFileName(nf.RelativePath), nf.RelativePath, nf.FileSize, nf.LastModifiedUtc, nf.FileHash, nf.OriginContentFileVersionId, false, false)); // Index removed
    }

    public async Task<NotebookFileDto> CreateTextFileAsync(
        Guid projectId,
        Guid notebookId,
        string relativePath,
        string content)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks.FindAsync(notebookId);
        if (notebook == null || notebook.ProjectId != projectId)
        {
            throw new ArgumentException("Notebook not found.");
        }

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        
        // Normalize relative path (ensure forward slashes)
        var normalizedPath = relativePath.Replace("\\", "/");

        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, normalizedPath, out var physicalPath))
        {
            throw new FileNotFoundException("File not found on disk.", normalizedPath);
        }
        
        // Ensure parent folder exists (e.g., /conversations)
        var parentFolder = Path.GetDirectoryName(physicalPath);
        if (!string.IsNullOrEmpty(parentFolder))
        {
            Directory.CreateDirectory(parentFolder);
        }

        // Write content to file
        await File.WriteAllTextAsync(physicalPath, content, System.Text.Encoding.UTF8);

        var fileInfo = new FileInfo(physicalPath);
        var fileHash = ComputeSha256(physicalPath);

        // Check if file already exists
        var existingFile = await context.NotebookFiles
            .FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == normalizedPath);

        NotebookFile notebookFile;

        if (existingFile != null)
        {
            // Update existing file
            existingFile.FileSize = fileInfo.Length;
            existingFile.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
            existingFile.FileHash = fileHash;
            notebookFile = existingFile;
        }
        else
        {
            // Create new file record
            notebookFile = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = normalizedPath,
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                FileHash = fileHash,
                OriginContentFileVersionId = null
            };
            notebookFile.GenerateDocumentId(notebookId);
            context.NotebookFiles.Add(notebookFile);
        }

        await context.SaveChangesAsync();

        // Record usage
        try
        {
            using var usageScope = _scopeFactory.CreateScope();
            var usageRecorder = usageScope.ServiceProvider.GetRequiredService<GuideAnts.Usage.IUsageRecorder>();
            await usageRecorder.RecordAsync(
                projectId: projectId,
                notebookId: notebookId,
                category: GuideAnts.Usage.UsageCategory.StorageUploaded,
                service: "Storage",
                operation: "create-text-file",
                metrics: new GuideAnts.Usage.UsageMetrics(ValueOther: notebookFile.FileSize),
                notebookFileId: notebookFile.Id,
                metadataJson: JsonSerializer.Serialize(new { path = notebookFile.RelativePath }));
        }
        catch { /* best-effort */ }

        // Record lineage
        await _lineageService.RecordAsync(
            FileKind.Notebook,
            projectId,
            notebookFile.Id,
            null,
            FileLineageAction.Uploaded,
            notebookId,
            physicalPath);

        // Create markdown shadow (for .md files, this is essentially a no-op but ensures consistency)
        try
        {
            await _markdownExtractionService.CreateNotebookMarkdownShadowAsync(notebookFile.Id);
            _logger.LogInformation("Created markdown shadow for saved conversation file {NotebookFileId} ({RelativePath})", 
                notebookFile.Id, LogValueSanitizer.Sanitize(notebookFile.RelativePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create markdown shadow for saved conversation file {RelativePath}", 
                LogValueSanitizer.Sanitize(notebookFile.RelativePath));
        }

        // Create indexing job for markdown file
        try
        {
            using var jobScope = _scopeFactory.CreateScope();
            var jobQueue = jobScope.ServiceProvider.GetRequiredService<GuideAntsApi.BackgroundJobs.IJobQueueService>();
            await jobQueue.EnqueueAsync(
                jobType: nameof(GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob).Replace("Job", string.Empty),
                payload: new GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileJob(notebookFile.Id, IsContentFile: false));
            _logger.LogInformation("Created indexing job for saved conversation file {NotebookFileId} ({RelativePath})", 
                notebookFile.Id, LogValueSanitizer.Sanitize(notebookFile.RelativePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create indexing job for saved conversation file {RelativePath}", 
                LogValueSanitizer.Sanitize(notebookFile.RelativePath));
        }

        await QueueNotebookSyncBestEffortAsync(notebookId);

        return new NotebookFileDto(
            notebookFile.Id,
            Path.GetFileName(notebookFile.RelativePath),
            notebookFile.RelativePath,
            notebookFile.FileSize,
            notebookFile.LastModifiedUtc,
            notebookFile.FileHash,
            notebookFile.OriginContentFileVersionId,
            false,
            false);
    }

    private async Task QueueNotebookSyncBestEffortAsync(Guid notebookId)
    {
        try
        {
            await _syncService.QueueReconcileAsync(notebookId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background sync queueing failed for notebook {NotebookId}", notebookId);
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

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
        return Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase);
    }

    private static bool IsInPycacheFolder(string relativePath)
    {
        // Check if the path is inside a __pycache__ folder
        return relativePath.StartsWith("__pycache__/") || 
               relativePath.Contains("/__pycache__/") || 
               relativePath.EndsWith("/__pycache__");
    }

    private HashSet<string> GetHiddenOutputProjectionPaths(
        Guid projectId,
        Guid notebookId,
        IReadOnlyCollection<NotebookFile> files)
    {
        var hiddenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0)
        {
            return hiddenPaths;
        }

        var resourceFileNames = files
            .Select(f => NormalizeRelativePath(f.RelativePath))
            .Where(IsInResourcesFolder)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (resourceFileNames.Count == 0)
        {
            return hiddenPaths;
        }

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        var resourcesRoot = Path.GetFullPath(Path.Combine(notebookRoot, "Resources"));

        foreach (var file in files)
        {
            var relativePath = NormalizeRelativePath(file.RelativePath);
            if (!IsInOutputFolder(relativePath))
            {
                continue;
            }

            var fileName = Path.GetFileName(relativePath);
            if (string.IsNullOrWhiteSpace(fileName) || !resourceFileNames.Contains(fileName))
            {
                continue;
            }

            if (!TryResolveNotebookPath(projectId, notebookId, relativePath, out var physicalPath) || !File.Exists(physicalPath))
            {
                continue;
            }

            if (IsProjectedOutputResourceSymlink(physicalPath, resourcesRoot))
            {
                hiddenPaths.Add(relativePath);
            }
        }

        return hiddenPaths;
    }

    private static bool IsInOutputFolder(string relativePath)
    {
        return relativePath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("Output", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace("\\", "/").TrimStart('/');

    private static bool IsProjectedOutputResourceSymlink(string physicalPath, string resourcesRoot)
    {
        if (!TryGetFileAttributes(physicalPath, out var attributes) || !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        var resolvedTarget = ResolveSymlinkTargetFullPath(physicalPath);
        if (string.IsNullOrWhiteSpace(resolvedTarget))
        {
            return false;
        }

        return IsPathUnderRoot(resolvedTarget, resourcesRoot);
    }

    private static bool TryGetFileAttributes(string physicalPath, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            attributes = File.GetAttributes(physicalPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveSymlinkTargetFullPath(string physicalPath)
    {
        try
        {
            var fileInfo = new FileInfo(physicalPath);
            var linkTarget = fileInfo.LinkTarget;
            if (!string.IsNullOrWhiteSpace(linkTarget))
            {
                var candidate = Path.IsPathRooted(linkTarget)
                    ? linkTarget
                    : Path.Combine(fileInfo.DirectoryName ?? Path.GetDirectoryName(physicalPath) ?? string.Empty, linkTarget);
                return Path.GetFullPath(candidate);
            }

            var resolved = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved != null)
            {
                return Path.GetFullPath(resolved.FullName);
            }
        }
        catch
        {
            // Best-effort only.
        }

        return null;
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        var rootFullPath = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFullPath = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (candidateFullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = rootFullPath + Path.DirectorySeparatorChar;
        var rootWithAltSeparator = rootFullPath + Path.AltDirectorySeparatorChar;
        return candidateFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
               candidateFullPath.StartsWith(rootWithAltSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a path is inside the Resources folder.
    /// Resources files are part of the guide definition and should be hidden from users
    /// and protected from modification/deletion via the API.
    /// </summary>
    private static bool IsInResourcesFolder(string relativePath)
    {
        return relativePath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("Resources", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInGuideantsFolder(string relativePath)
    {
        return relativePath.StartsWith(".guideants/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(".guideants", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureNotRegisteredMountRootAsync(
        ApplicationDbContext context,
        Guid notebookId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = relativePath.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        var normalizedLower = normalized.ToLowerInvariant();
        var isActiveMountRoot = await context.HostFolderMountLinks
            .AsNoTracking()
            .Where(l => l.NotebookId == notebookId
                        && l.LinkRelativePath.ToLower() == normalizedLower)
            .Join(
                context.HostFolderMounts.AsNoTracking(),
                link => link.HostFolderMountId,
                mount => mount.Id,
                (_, mount) => mount)
            .AnyAsync(m => m.Status != HostFolderMountStatus.Removed, cancellationToken);

        if (isActiveMountRoot)
        {
            throw new InvalidOperationException(
                "This folder is a host folder mount root and cannot be deleted normally. Use Remove mapped folder to unlink it without deleting host contents.");
        }
    }

    public async Task<NotebookFolderTreeDto?> CreateFolderAsync(Guid projectId, Guid notebookId, string newFolderPath)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks.FindAsync(notebookId);
        if (notebook == null || notebook.ProjectId != projectId)
        {
            throw new ArgumentException("Notebook not found.");
        }

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        if (!NotebookStoragePath.TryResolveDirectoryUnderRoot(notebookRoot, newFolderPath, out var physicalPath))
        {
            return null;
        }

        if (File.Exists(physicalPath))
        {
            _logger.LogWarning("Cannot create folder at {Path}; a file exists at this path.", LogValueSanitizer.Sanitize(physicalPath));
            return null;
        }

        // If directory already exists, treat as success (idempotent)
        if (!Directory.Exists(physicalPath))
        {
            Directory.CreateDirectory(physicalPath);
        }
        _logger.LogInformation(
            "Created folder {FolderPath} in notebook {NotebookId}",
            LogValueSanitizer.Sanitize(newFolderPath),
            LogValueSanitizer.Sanitize(notebookId));
        
        // Return the current folder tree - the new empty folder will be managed client-side
        // until files are added to it
        return await GetFolderTreeAsync(projectId, notebookId);
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid notebookId, string relativePath)
    {

// Resources files are part of the guide definition and cannot be deleted
        if (IsInResourcesFolder(relativePath) || IsInGuideantsFolder(relativePath))
        {
            throw new InvalidOperationException("Resource files cannot be deleted. They are part of the guide definition.");
        }

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        await EnsureNotRegisteredMountRootAsync(context, notebookId, relativePath);

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, relativePath, out var physicalPath))
        {
            return false;
        }

        if (File.Exists(physicalPath))
        {
            // --- Deleting a single file ---
            var dbFile = await context.NotebookFiles.FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == relativePath);
            if (dbFile != null)
            {
                // Pre-check: if any project content versions reference this notebook file, block deletion
                var referencingCount = await context.ContentFileVersions.CountAsync(v => v.OriginNotebookFileId == dbFile.Id);
                if (referencingCount > 0)
                {
                    throw new InvalidOperationException("This notebook file cannot be deleted because it is referenced by one or more project file versions. Remove or detach those published versions before deleting.");
                }

                // Handle indexing removal based on file origin
                if (dbFile.OriginContentFileVersionId.HasValue)
                {
                    // This is a PROJECT FILE COPY - update tags to remove notebook reference
                    // DO NOT delete shadow files (they belong to the original project file)
                    try
                    {
                        using var shadowScope = _scopeFactory.CreateScope();
                // Kernel Memory removed - shadowIndexer no longer needed
                        // No action needed - Kernel Memory removed
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error during file removal cleanup {RelativePath}", LogValueSanitizer.Sanitize(dbFile.RelativePath));
                    }
                }
                else
                {
                    // This is a NOTEBOOK-NATIVE FILE - delete entirely from index and clean up shadows
                    try
                    {
                        using var shadowScope = _scopeFactory.CreateScope();
                // Kernel Memory removed - shadowIndexer no longer needed
                        // No action needed - Kernel Memory removed
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error during notebook-native file cleanup {DocId}", dbFile.DocumentId);
                    }

                    // Delete associated shadow files (only for notebook-native files)
                    var shadowFiles = await context.NotebookFileMarkdownShadows
                        .Where(s => s.OriginalNotebookFileId == dbFile.Id)
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
                                _logger.LogError(ex, "Failed to delete shadow file {ShadowPath}: {Error}", LogValueSanitizer.Sanitize(shadow.StoragePath), LogValueSanitizer.Sanitize(ex.Message));
                            }
                        }
                    }
                }

                context.NotebookFiles.Remove(dbFile);
                await _lineageService.RecordAsync(
            FileKind.Notebook,
                    projectId,
                    dbFile.Id,
                    null,
                    FileLineageAction.Deleted,
                    notebookId,
                    physicalPath);
            }
            // Delete physical file only after DB change validation
            File.Delete(physicalPath);
        }
        else if (Directory.Exists(physicalPath))
        {
            // --- Deleting a folder ---
            var normalizedPath = relativePath.Replace("\\", "/");
            var normalizedPathWithSlash = normalizedPath.EndsWith("/") ? normalizedPath : normalizedPath + "/";
            
            var dbFiles = await context.NotebookFiles
                .Where(f => f.NotebookId == notebookId && f.RelativePath.StartsWith(normalizedPathWithSlash))
                .ToListAsync();

            // Pre-check: block deletion if any file in folder is referenced by project versions
            var anyReferenced = dbFiles.Count > 0 && await context.ContentFileVersions.AnyAsync(v => dbFiles.Select(df => df.Id).Contains(v.OriginNotebookFileId ?? Guid.Empty));
            if (anyReferenced)
            {
                throw new InvalidOperationException("One or more files in this folder are referenced by project file versions. Remove or detach those published versions before deleting the folder.");
            }

            // Delete all files from Kernel Memory and handle shadow cleanup
            foreach (var dbFile in dbFiles)
            {
                // Handle indexing removal based on file origin
                if (dbFile.OriginContentFileVersionId.HasValue)
                {
                    // This is a PROJECT FILE COPY - update tags to remove notebook reference
                    // DO NOT delete shadow files (they belong to the original project file)
                    try
                    {
                        using var shadowScope = _scopeFactory.CreateScope();
                // Kernel Memory removed - shadowIndexer no longer needed
                        // No action needed - Kernel Memory removed
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error during file removal cleanup {RelativePath}", LogValueSanitizer.Sanitize(dbFile.RelativePath));
                    }
                }
                else
                {
                    // This is a NOTEBOOK-NATIVE FILE - delete entirely from index and clean up shadows
                    try
                    {
                        using var shadowScope = _scopeFactory.CreateScope();
                // Kernel Memory removed - shadowIndexer no longer needed
                        // No action needed - Kernel Memory removed
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error during notebook-native file cleanup {DocId}", dbFile.DocumentId);
                    }

                    // Delete associated shadow files (only for notebook-native files)
                    var shadowFiles = await context.NotebookFileMarkdownShadows
                        .Where(s => s.OriginalNotebookFileId == dbFile.Id)
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
                                _logger.LogError(ex, "Failed to delete shadow file {ShadowPath}: {Error}", LogValueSanitizer.Sanitize(shadow.StoragePath), LogValueSanitizer.Sanitize(ex.Message));
                            }
                        }
                    }
                }
            }
            
            context.NotebookFiles.RemoveRange(dbFiles);
            Directory.Delete(physicalPath, recursive: true);
        }
        else
        {
            // Physical path is missing. Perform a soft-delete by removing DB rows and cleaning indexes/shadows.
            var normalizedRelPath = relativePath.Replace("\\", "/");
            // Try exact file match first
            var dbFileMissing = await context.NotebookFiles
                .FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == normalizedRelPath);

            if (dbFileMissing != null)
            {
                // Block soft-delete if referenced by project versions
                var referencingCount = await context.ContentFileVersions.CountAsync(v => v.OriginNotebookFileId == dbFileMissing.Id);
                if (referencingCount > 0)
                {
                    throw new InvalidOperationException("This notebook file cannot be deleted because it is referenced by one or more project file versions. Remove or detach those published versions before deleting.");
                }

                if (!dbFileMissing.OriginContentFileVersionId.HasValue)
                {
                    var missingShadows = await context.NotebookFileMarkdownShadows
                        .Where(s => s.OriginalNotebookFileId == dbFileMissing.Id)
                        .ToListAsync();
                    foreach (var shadow in missingShadows)
                    {
                        if (!string.IsNullOrEmpty(shadow.StoragePath) && File.Exists(shadow.StoragePath))
                        {
                            try
                            {
                                File.Delete(shadow.StoragePath);
                                var shadowDir = Path.GetDirectoryName(shadow.StoragePath);
                                if (Directory.Exists(shadowDir) && !Directory.EnumerateFileSystemEntries(shadowDir).Any())
                                {
                                    Directory.Delete(shadowDir);
                                    var parentShadowDir = Path.GetDirectoryName(shadowDir);
                                    if (Directory.Exists(parentShadowDir) && !Directory.EnumerateFileSystemEntries(parentShadowDir).Any())
                                    {
                                        Directory.Delete(parentShadowDir);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to delete shadow file {ShadowPath}: {Error}", LogValueSanitizer.Sanitize(shadow.StoragePath), LogValueSanitizer.Sanitize(ex.Message));
                            }
                        }
                    }
                }

                context.NotebookFiles.Remove(dbFileMissing);
                await _lineageService.RecordAsync(
            FileKind.Notebook,
                    projectId,
                    dbFileMissing.Id,
                    null,
                    FileLineageAction.Deleted,
                    notebookId,
                    physicalPath);

                _logger.LogInformation("Soft-deleted DB record for missing notebook file {RelativePath}", LogValueSanitizer.Sanitize(normalizedRelPath));
            }
            else
            {
                // Treat as folder: delete any DB files under this prefix
                var prefix = normalizedRelPath.EndsWith("/") ? normalizedRelPath : normalizedRelPath + "/";
                var dbFilesMissingFolder = await context.NotebookFiles
                    .Where(f => f.NotebookId == notebookId && f.RelativePath.StartsWith(prefix))
                    .ToListAsync();

                if (dbFilesMissingFolder.Count > 0)
                {
                    // Block soft-delete if any are referenced
                    var anyReferenced = await context.ContentFileVersions.AnyAsync(v => dbFilesMissingFolder.Select(df => df.Id).Contains(v.OriginNotebookFileId ?? Guid.Empty));
                    if (anyReferenced)
                    {
                        throw new InvalidOperationException("One or more files in this folder are referenced by project file versions. Remove or detach those published versions before deleting the folder.");
                    }

                    foreach (var dbf in dbFilesMissingFolder)
                    {
                        if (dbf.OriginContentFileVersionId.HasValue)
                        {
                            try
                            {
                                using var shadowScope = _scopeFactory.CreateScope();
                // Kernel Memory removed - shadowIndexer no longer needed
                                // No action needed - Kernel Memory removed
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to update notebook tags after removing file {RelativePath}", LogValueSanitizer.Sanitize(dbf.RelativePath));
                            }
                        }
                        else
                        {
                            try
                            {
                                using var shadowScope = _scopeFactory.CreateScope();
                // Kernel Memory removed - shadowIndexer no longer needed
                                // No action needed - Kernel Memory removed
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to delete notebook-native document {DocId} from KM", dbf.DocumentId);
                            }

                            var shadows = await context.NotebookFileMarkdownShadows
                                .Where(s => s.OriginalNotebookFileId == dbf.Id)
                                .ToListAsync();
                            foreach (var shadow in shadows)
                            {
                                if (!string.IsNullOrEmpty(shadow.StoragePath) && File.Exists(shadow.StoragePath))
                                {
                                    try
                                    {
                                        File.Delete(shadow.StoragePath);
                                        var shadowDir = Path.GetDirectoryName(shadow.StoragePath);
                                        if (Directory.Exists(shadowDir) && !Directory.EnumerateFileSystemEntries(shadowDir).Any())
                                        {
                                            Directory.Delete(shadowDir);
                                            var parentShadowDir = Path.GetDirectoryName(shadowDir);
                                            if (Directory.Exists(parentShadowDir) && !Directory.EnumerateFileSystemEntries(parentShadowDir).Any())
                                            {
                                                Directory.Delete(parentShadowDir);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Failed to delete shadow file {ShadowPath}: {Error}", LogValueSanitizer.Sanitize(shadow.StoragePath), LogValueSanitizer.Sanitize(ex.Message));
                                    }
                                }
                            }
                        }

                        await _lineageService.RecordAsync(
            FileKind.Notebook,
                            projectId,
                            dbf.Id,
                            null,
                            FileLineageAction.Deleted,
                            notebookId,
                            physicalPath);
                    }

                    context.NotebookFiles.RemoveRange(dbFilesMissingFolder);
                    _logger.LogInformation("Soft-deleted {Count} DB records for missing notebook folder {RelativePath}", dbFilesMissingFolder.Count, LogValueSanitizer.Sanitize(normalizedRelPath));
                }
                else
                {
                    // Nothing to do; treat as idempotent success
                    _logger.LogInformation("Delete requested for {Path} but nothing found on disk or in DB; treating as success.", LogValueSanitizer.Sanitize(physicalPath));
                }
            }

            // Do not return here; let SaveChangesAsync() below persist removals and return success.
        }

        await context.SaveChangesAsync();
        
        await QueueNotebookSyncBestEffortAsync(notebookId);
        await InvalidateMountCachesForNotebookPathAsync(notebookId, relativePath);
        
        return true;
    }

    public async Task<bool> RenameAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string newName)
    {

// Resources files are part of the guide definition and cannot be renamed
        if (IsInResourcesFolder(sourceRelativePath) || IsInGuideantsFolder(sourceRelativePath))
        {
            throw new InvalidOperationException("Resource files cannot be renamed. They are part of the guide definition.");
        }

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, sourceRelativePath, out var sourcePhysicalPath))
        {
            return false;
        }

        var safeNewName = NotebookStoragePath.SanitizeFileName(newName);
        if (safeNewName == null)
        {
            return false;
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePhysicalPath);
        if (string.IsNullOrEmpty(sourceDirectory))
        {
            return false;
        }

        if (!NotebookStoragePath.TryResolveUnderRoot(sourceDirectory, safeNewName, out var newPhysicalPath))
        {
            return false;
        }

        if (newPhysicalPath == sourcePhysicalPath) return true; // No change
        if (File.Exists(newPhysicalPath) || Directory.Exists(newPhysicalPath)) return false; // Conflict

        if (File.Exists(sourcePhysicalPath))
        {
            var dbFile = await context.NotebookFiles.FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == sourceRelativePath);

            File.Move(sourcePhysicalPath, newPhysicalPath);

            if (dbFile != null)
            {
                dbFile.RelativePath = Path.GetRelativePath(notebookRoot, newPhysicalPath).Replace("\\", "/");
                dbFile.GenerateDocumentId(notebookId);
                await _lineageService.RecordAsync(
                    FileKind.Notebook,
                    projectId,
                    dbFile.Id,
                    null,
                    FileLineageAction.Renamed,
                    notebookId,
                    newPhysicalPath);
            }
        }
        else if (Directory.Exists(sourcePhysicalPath))
        {
            Directory.Move(sourcePhysicalPath, newPhysicalPath);
            var oldPrefix = sourceRelativePath.EndsWith("/") ? sourceRelativePath : sourceRelativePath + "/";
            var newPrefix = Path.GetRelativePath(notebookRoot, newPhysicalPath).Replace("\\", "/") + "/";
            var affectedFiles = await context.NotebookFiles.Where(f => f.NotebookId == notebookId && f.RelativePath.StartsWith(oldPrefix)).ToListAsync();
            foreach(var file in affectedFiles)
            {
                file.RelativePath = newPrefix + file.RelativePath.Substring(oldPrefix.Length);
                file.GenerateDocumentId(notebookId);
            }
        }
        else
        {
            return false;
        }

        await context.SaveChangesAsync();
        
        await QueueNotebookSyncBestEffortAsync(notebookId);
        await InvalidateMountCachesForNotebookPathAsync(notebookId, sourceRelativePath);
        
        return true;
    }

    public async Task<bool> MoveAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string destinationRelativePath)
    {

// Resources files are part of the guide definition and cannot be moved
        if (IsInResourcesFolder(sourceRelativePath) || IsInGuideantsFolder(sourceRelativePath))
        {
            throw new InvalidOperationException("Resource files cannot be moved. They are part of the guide definition.");
        }

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, sourceRelativePath, out var sourcePhysicalPath))
        {
            return false;
        }

        if (!NotebookStoragePath.TryResolveDirectoryUnderRoot(notebookRoot, destinationRelativePath, out var destDirectoryPhysicalPath))
        {
            return false;
        }

        if (!NotebookStoragePath.TryResolveUnderRoot(destDirectoryPhysicalPath, Path.GetFileName(sourcePhysicalPath), out var newPhysicalPath))
        {
            return false;
        }

        if (newPhysicalPath == sourcePhysicalPath) return true;
        if (File.Exists(newPhysicalPath) || Directory.Exists(newPhysicalPath)) return false;
        if (destinationRelativePath.StartsWith(sourceRelativePath)) return false; // Cannot move a folder into itself

        Directory.CreateDirectory(destDirectoryPhysicalPath);

        if (File.Exists(sourcePhysicalPath))
        {
            var dbFile = await context.NotebookFiles.FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == sourceRelativePath);
            if (dbFile == null) return false;

            File.Move(sourcePhysicalPath, newPhysicalPath);
            dbFile.RelativePath = Path.GetRelativePath(notebookRoot, newPhysicalPath).Replace("\\", "/");
            // Recalculate DocumentId after path change
            dbFile.GenerateDocumentId(notebookId);
            await _lineageService.RecordAsync(
            FileKind.Notebook,
                projectId,
                dbFile.Id,
                null,
                FileLineageAction.Moved,
                notebookId,
                newPhysicalPath);
        }
        else if (Directory.Exists(sourcePhysicalPath))
        {
            Directory.Move(sourcePhysicalPath, newPhysicalPath);
            var oldPrefix = sourceRelativePath.EndsWith("/") ? sourceRelativePath : sourceRelativePath + "/";
            var newPrefix = Path.GetRelativePath(notebookRoot, newPhysicalPath).Replace("\\", "/") + "/";
            var affectedFiles = await context.NotebookFiles.Where(f => f.NotebookId == notebookId && f.RelativePath.StartsWith(oldPrefix)).ToListAsync();
            foreach(var file in affectedFiles)
            {
                file.RelativePath = newPrefix + file.RelativePath.Substring(oldPrefix.Length);
                file.GenerateDocumentId(notebookId);
            }
        }
        else
        {
            return false;
        }

        await context.SaveChangesAsync();
        
        await QueueNotebookSyncBestEffortAsync(notebookId);
        await InvalidateMountCachesForNotebookPathAsync(notebookId, sourceRelativePath);
        await InvalidateMountCachesForNotebookPathAsync(notebookId, destinationRelativePath);
        
        return true;
    }



    public async Task<GuideAntsApi.Endpoints.OriginFileInfoDto?> GetOriginFileInfoAsync(Guid projectId, Guid contentFileVersionId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var version = await context.ContentFileVersions
            .Include(v => v.ContentFile)
            .ThenInclude(cf => cf.Folder)
            .FirstOrDefaultAsync(v => v.Id == contentFileVersionId);

        if (version == null || version.ContentFile.ProjectId != projectId) return null;

        var folderPath = version.ContentFile.Folder?.Name ?? "Project Root";
        // If there are nested folders, we might need to build the full path
        if (version.ContentFile.Folder != null)
        {
            var folders = new List<string>();
            var currentFolder = version.ContentFile.Folder;
            while (currentFolder != null)
            {
                folders.Insert(0, currentFolder.Name);
                if (currentFolder.ParentFolderId.HasValue)
                {
                    currentFolder = await context.ProjectFolders
                        .FirstOrDefaultAsync(f => f.Id == currentFolder.ParentFolderId.Value);
                }
                else
                {
                    break;
                }
            }
            folderPath = string.Join(" / ", folders);
        }

                 return new GuideAntsApi.Endpoints.OriginFileInfoDto
         {
             FileName = version.ContentFile.FileName,
             FolderPath = folderPath,
             ContentFileId = version.ContentFile.Id,
             VersionNumber = version.VersionNumber
         };
     }

    #region By-ID Operations (No tree lookup required)

    public async Task<bool> DeleteByIdAsync(Guid projectId, Guid notebookId, Guid fileId)
    {
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Query file by ID to get relativePath
        var dbFile = await context.NotebookFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.NotebookId == notebookId);
        
        if (dbFile == null) return false;
        
        // Use existing DeleteAsync with the retrieved path
        return await DeleteAsync(projectId, notebookId, dbFile.RelativePath);
    }

    public async Task<bool> RenameByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string newName)
    {
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Query file by ID to get relativePath
        var dbFile = await context.NotebookFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.NotebookId == notebookId);
        
        if (dbFile == null) return false;
        
        // Use existing RenameAsync with the retrieved path
        return await RenameAsync(projectId, notebookId, dbFile.RelativePath, newName);
    }

    public async Task<bool> MoveByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string? destinationPath)
    {
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        // Query file by ID to get relativePath
        var dbFile = await context.NotebookFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.NotebookId == notebookId);
        
        if (dbFile == null) return false;
        
        // Use existing MoveAsync with the retrieved path
        // If destinationPath is null, use root directory
        var destPath = destinationPath ?? "";
        return await MoveAsync(projectId, notebookId, dbFile.RelativePath, destPath);
    }

    #endregion

    public async Task<NotebookFile?> GetNotebookFile(Guid fileId, Guid notebookId)
    {
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        return await context.NotebookFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.NotebookId == notebookId);
    }
}
