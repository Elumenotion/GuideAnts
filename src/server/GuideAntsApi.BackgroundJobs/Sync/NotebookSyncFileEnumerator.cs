namespace GuideAntsApi.BackgroundJobs.Sync;

/// <summary>
/// Mount-aware notebook file enumeration shared by both sync paths (plan §14, D1).
/// Registered mount roots are first-class folder entries; their contents are not descended into.
/// </summary>
public static class NotebookSyncFileEnumerator
{
    public static IReadOnlyList<string> EnumerateSyncableRelativePaths(
        string notebookRoot,
        Func<string, bool>? fileNameFilter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notebookRoot);
        if (!Directory.Exists(notebookRoot))
        {
            return [];
        }

        var registeredMountRoots = NotebookMountsRegistryLoader.LoadRegisteredMountRelativePaths(notebookRoot);
        var results = new List<string>();
        EnumerateDirectory(notebookRoot, notebookRoot, registeredMountRoots, results, fileNameFilter);
        return results;
    }

    public static bool IsUnderRegisteredMount(string relativePath, string notebookRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(notebookRoot);

        var normalized = NormalizeRelativePath(relativePath);
        foreach (var mountRoot in NotebookMountsRegistryLoader.LoadRegisteredMountRelativePaths(notebookRoot))
        {
            if (IsUnderMountRoot(normalized, mountRoot))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsRegisteredMountRoot(string relativePath, string notebookRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(notebookRoot);

        var normalized = NormalizeRelativePath(relativePath);
        return NotebookMountsRegistryLoader
            .LoadRegisteredMountRelativePaths(notebookRoot)
            .Contains(normalized);
    }

    private static void EnumerateDirectory(
        string notebookRoot,
        string currentDirectory,
        HashSet<string> registeredMountRoots,
        List<string> results,
        Func<string, bool>? fileNameFilter)
    {
        foreach (var filePath in Directory.EnumerateFiles(currentDirectory))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(notebookRoot, filePath));
            if (IsInGuideantsFolder(relativePath))
            {
                continue;
            }

            if (ShouldSkipProjectedOutputResourceSymlink(notebookRoot, filePath, relativePath))
            {
                continue;
            }

            if (fileNameFilter != null && !fileNameFilter(Path.GetFileName(filePath)))
            {
                continue;
            }

            results.Add(relativePath);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(currentDirectory))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(notebookRoot, directoryPath));
            if (IsInGuideantsFolder(relativePath))
            {
                continue;
            }

            if (ShouldSkipMountDescent(directoryPath, relativePath, registeredMountRoots))
            {
                continue;
            }

            EnumerateDirectory(notebookRoot, directoryPath, registeredMountRoots, results, fileNameFilter);
        }
    }

    internal static bool ShouldSkipMountDescent(
        string fullDirectoryPath,
        string relativePath,
        HashSet<string> registeredMountRoots)
    {
        if (registeredMountRoots.Contains(relativePath))
        {
            return true;
        }

        if (TryGetReparsePoint(fullDirectoryPath, out var isReparsePoint) && isReparsePoint)
        {
            return true;
        }

        return false;
    }

    internal static bool TryGetReparsePoint(string path, out bool isReparsePoint)
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

    private static bool ShouldSkipProjectedOutputResourceSymlink(
        string notebookRoot,
        string filePath,
        string relativePath)
    {
        if (!IsInOutputFolder(relativePath))
        {
            return false;
        }

        if (!TryGetReparsePoint(filePath, out var isReparsePoint) || !isReparsePoint)
        {
            return false;
        }

        var targetPath = ResolveSymlinkTargetFullPath(filePath);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var resourcesRoot = Path.GetFullPath(Path.Combine(notebookRoot, "Resources"));
        return IsPathUnderRoot(targetPath, resourcesRoot);
    }

    private static bool IsInOutputFolder(string relativePath) =>
        relativePath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("Output", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveSymlinkTargetFullPath(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            var linkTarget = info.LinkTarget;
            if (!string.IsNullOrWhiteSpace(linkTarget))
            {
                var candidate = Path.IsPathRooted(linkTarget)
                    ? linkTarget
                    : Path.Combine(info.DirectoryName ?? Path.GetDirectoryName(filePath) ?? string.Empty, linkTarget);
                return Path.GetFullPath(candidate);
            }

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
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

    private static bool IsUnderMountRoot(string relativePath, string mountRoot)
    {
        if (relativePath.Equals(mountRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relativePath.StartsWith(mountRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInGuideantsFolder(string relativePath) =>
        relativePath.StartsWith(".guideants/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals(".guideants", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');
}
