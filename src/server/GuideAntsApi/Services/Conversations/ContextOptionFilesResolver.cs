using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations;

internal static class ContextOptionFilesResolver
{
    public static async Task<IReadOnlyList<string>> ResolvePathsAsync(
        ApplicationDbContext db,
        IStoragePathResolver pathResolver,
        Guid projectId,
        Guid notebookId,
        bool isPublished,
        CancellationToken ct = default)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var notebookRootPaths = await db.NotebookFiles
            .AsNoTracking()
            .Where(f => f.NotebookId == notebookId)
            .Select(f => f.RelativePath)
            .ToListAsync(ct);

        foreach (var notebookRootPath in notebookRootPaths)
        {
            if (IsInResourcesFolder(notebookRootPath)
                || IsInGuideantsFolder(notebookRootPath)
                || NotebookArtifactPathExclusions.IsExcludedRelativePath(notebookRootPath))
            {
                continue;
            }

            paths.Add(ToCwdRelativePath(notebookRootPath, isPublished));
        }

        var notebookRoot = pathResolver.GetNotebookRootPath(projectId, notebookId);
        AppendOutputSymlinkPaths(notebookRoot, isPublished, paths);
        await AppendLinkedMountShallowPathsAsync(db, notebookId, isPublished, paths, ct);

        return paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string ToCwdRelativePath(string notebookRootPath, bool isPublished)
    {
        if (string.IsNullOrWhiteSpace(notebookRootPath))
        {
            return notebookRootPath;
        }

        var normalized = notebookRootPath.Replace('\\', '/').Trim().TrimStart('/');

        if (isPublished)
        {
            return $"../../{normalized}";
        }

        const string outputPrefix = "Output/";
        if (normalized.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[outputPrefix.Length..];
        }

        return $"../{normalized}";
    }

    private static void AppendOutputSymlinkPaths(string notebookRoot, bool isPublished, HashSet<string> paths)
    {
        var outputDir = Path.Combine(notebookRoot, "Output");
        if (!Directory.Exists(outputDir))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
        {
            if (!TryGetReparsePoint(filePath, out var isReparsePoint) || !isReparsePoint)
            {
                continue;
            }

            var relativeFromOutput = Path.GetRelativePath(outputDir, filePath).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relativeFromOutput))
            {
                continue;
            }

            paths.Add(ToCwdRelativePath($"Output/{relativeFromOutput}", isPublished));
        }
    }

    private static async Task AppendLinkedMountShallowPathsAsync(
        ApplicationDbContext db,
        Guid notebookId,
        bool isPublished,
        HashSet<string> paths,
        CancellationToken ct)
    {
        var mountRoots = await GetLinkedMountRootsAsync(db, notebookId, ct);
        foreach (var mountRoot in mountRoots)
        {
            AppendMountRootShallowListing(mountRoot.LinkRelativePath, mountRoot.LinkPhysicalPath, isPublished, paths);
        }
    }

    private static void AppendMountRootShallowListing(
        string mountNotebookRelativePath,
        string mountPhysicalPath,
        bool isPublished,
        HashSet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(mountNotebookRelativePath)
            || string.IsNullOrWhiteSpace(mountPhysicalPath)
            || !Directory.Exists(mountPhysicalPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(mountPhysicalPath))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var notebookPath = $"{mountNotebookRelativePath}/{fileName}".Replace('\\', '/');
            if (NotebookArtifactPathExclusions.IsExcludedRelativePath(notebookPath))
            {
                continue;
            }

            paths.Add(ToCwdRelativePath(notebookPath, isPublished));
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(mountPhysicalPath))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                continue;
            }

            var notebookPath = $"{mountNotebookRelativePath}/{directoryName}/".Replace('\\', '/');
            if (NotebookArtifactPathExclusions.IsExcludedRelativePath(notebookPath.TrimEnd('/')))
            {
                continue;
            }

            paths.Add(ToCwdRelativePath(notebookPath.TrimEnd('/'), isPublished) + "/");
        }
    }

    private static async Task<List<LinkedMountRoot>> GetLinkedMountRootsAsync(
        ApplicationDbContext db,
        Guid notebookId,
        CancellationToken ct)
    {
        var roots = await db.HostFolderMountLinks
            .AsNoTracking()
            .Where(link => link.NotebookId == notebookId && link.Status == HostFolderMountLinkStatus.Linked)
            .Join(
                db.HostFolderMounts.AsNoTracking(),
                link => link.HostFolderMountId,
                mount => mount.Id,
                (link, mount) => new { Link = link, Mount = mount })
            .Where(row => row.Mount.Status != HostFolderMountStatus.Removed)
            .Select(row => new LinkedMountRoot(
                row.Link.LinkRelativePath.Replace("\\", "/").Trim('/'),
                row.Link.LinkPhysicalPath))
            .ToListAsync(ct);

        var deduped = new Dictionary<string, LinkedMountRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.LinkRelativePath))
            {
                continue;
            }

            deduped.TryAdd(root.LinkRelativePath, root);
        }

        return deduped.Values.ToList();
    }

    private static bool TryGetReparsePoint(string path, out bool isReparsePoint)
    {
        isReparsePoint = false;
        try
        {
            isReparsePoint = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInResourcesFolder(string relativePath) =>
        relativePath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("Resources", StringComparison.OrdinalIgnoreCase);

    private static bool IsInGuideantsFolder(string relativePath) =>
        relativePath.StartsWith(".guideants/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals(".guideants", StringComparison.OrdinalIgnoreCase);

    private sealed record LinkedMountRoot(string LinkRelativePath, string LinkPhysicalPath);
}
