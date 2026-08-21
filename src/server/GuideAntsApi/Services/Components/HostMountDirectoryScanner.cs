using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Components;

public static class HostMountDirectoryScanner
{
    public sealed record ScannedFile(
        string RelativePath,
        string FileName,
        long FileSize,
        DateTime LastModifiedUtc);

    public sealed record ScannedDirectory(
        string RelativePath,
        string Name);

    public sealed record ScanResult(
        IReadOnlyList<ScannedFile> Files,
        IReadOnlyList<ScannedDirectory> Directories,
        bool WasTruncated);

    public sealed record MountRoot(
        string RelativePathPrefix,
        string PhysicalPath);

    public static ScanResult Scan(
        IReadOnlyList<MountRoot> roots,
        int maxFiles,
        int maxDepth,
        TimeSpan scanBudget,
        ILogger? logger = null)
    {
        var files = new List<ScannedFile>();
        var directories = new List<ScannedDirectory>();
        var seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var scanDeadlineUtc = DateTimeOffset.UtcNow + scanBudget;
        var fileEnumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MaxRecursionDepth = maxDepth
        };
        var directoryEnumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            // Include the frontier directory level so stubs exist for lazy expand.
            MaxRecursionDepth = Math.Max(0, maxDepth)
        };

        foreach (var root in roots)
        {
            if (DateTimeOffset.UtcNow >= scanDeadlineUtc || files.Count >= maxFiles)
            {
                truncated = true;
                break;
            }

            if (string.IsNullOrWhiteSpace(root.PhysicalPath) || !Directory.Exists(root.PhysicalPath))
            {
                continue;
            }

            try
            {
                foreach (var physicalDirectory in Directory.EnumerateDirectories(
                             root.PhysicalPath, "*", directoryEnumerationOptions))
                {
                    if (DateTimeOffset.UtcNow >= scanDeadlineUtc)
                    {
                        truncated = true;
                        break;
                    }

                    var relativeWithinMount = Path.GetRelativePath(root.PhysicalPath, physicalDirectory)
                        .Replace("\\", "/").TrimStart('/');
                    if (string.IsNullOrWhiteSpace(relativeWithinMount) || relativeWithinMount == ".")
                    {
                        continue;
                    }

                    // Keep directory stubs inside the initial window only (same depth as files).
                    var directoryDepth = relativeWithinMount.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
                    if (directoryDepth > maxDepth)
                    {
                        continue;
                    }

                    var fullRelativePath = $"{root.RelativePathPrefix}/{relativeWithinMount}".Replace("\\", "/");
                    var directoryName = Path.GetFileName(fullRelativePath);
                    if (IsExcludedDirectoryName(directoryName) || IsInPycacheFolder(fullRelativePath))
                    {
                        continue;
                    }

                    if (!seenDirPaths.Add(fullRelativePath))
                    {
                        continue;
                    }

                    directories.Add(new ScannedDirectory(
                        RelativePath: fullRelativePath,
                        Name: directoryName));
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Skipping mount directory enumeration for root {MountRoot}", root.RelativePathPrefix);
            }

            IEnumerable<string> physicalFiles;
            try
            {
                physicalFiles = Directory.EnumerateFiles(root.PhysicalPath, "*", fileEnumerationOptions);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Skipping mount file enumeration for root {MountRoot}", root.RelativePathPrefix);
                continue;
            }

            foreach (var physicalFile in physicalFiles)
            {
                if (DateTimeOffset.UtcNow >= scanDeadlineUtc || files.Count >= maxFiles)
                {
                    truncated = true;
                    break;
                }

                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(physicalFile);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                var relativeWithinMount = Path.GetRelativePath(root.PhysicalPath, physicalFile)
                    .Replace("\\", "/").TrimStart('/');
                if (string.IsNullOrWhiteSpace(relativeWithinMount) || relativeWithinMount == ".")
                {
                    continue;
                }

                var fullRelativePath = $"{root.RelativePathPrefix}/{relativeWithinMount}".Replace("\\", "/");
                var fileName = Path.GetFileName(fullRelativePath);

                if (IsTemporaryScriptFile(fileName)
                    || IsInPycacheFolder(fullRelativePath))
                {
                    continue;
                }

                if (!seenFilePaths.Add(fullRelativePath))
                {
                    continue;
                }

                files.Add(new ScannedFile(
                    RelativePath: fullRelativePath,
                    FileName: fileName,
                    FileSize: fileInfo.Length,
                    LastModifiedUtc: fileInfo.LastWriteTimeUtc));
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        return new ScanResult(files, directories, truncated);
    }

    /// <summary>
    /// Lists one directory level under <paramref name="relativeWithinMount"/> (empty = mount root).
    /// Paths in the result are prefixed with <paramref name="relativePathPrefix"/>.
    /// </summary>
    public static ScanResult ListLevel(
        string physicalRoot,
        string relativePathPrefix,
        string relativeWithinMount,
        int maxFiles,
        TimeSpan scanBudget,
        ILogger? logger = null)
    {
        var files = new List<ScannedFile>();
        var directories = new List<ScannedDirectory>();
        var truncated = false;
        var scanDeadlineUtc = DateTimeOffset.UtcNow + scanBudget;

        if (string.IsNullOrWhiteSpace(physicalRoot) || !Directory.Exists(physicalRoot))
        {
            return new ScanResult(files, directories, WasTruncated: false);
        }

        var normalizedWithin = NormalizeRelative(relativeWithinMount);
        var targetPhysical = string.IsNullOrEmpty(normalizedWithin)
            ? physicalRoot
            : Path.GetFullPath(Path.Combine(physicalRoot, normalizedWithin.Replace('/', Path.DirectorySeparatorChar)));

        var physicalRootFull = Path.GetFullPath(physicalRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsPathUnderRoot(targetPhysical, physicalRootFull) || !Directory.Exists(targetPhysical))
        {
            return new ScanResult(files, directories, WasTruncated: false);
        }

        var prefix = NormalizeRelative(relativePathPrefix);

        try
        {
            foreach (var physicalDirectory in Directory.EnumerateDirectories(targetPhysical))
            {
                if (DateTimeOffset.UtcNow >= scanDeadlineUtc)
                {
                    truncated = true;
                    break;
                }

                var name = Path.GetFileName(physicalDirectory);
                if (IsExcludedDirectoryName(name))
                {
                    continue;
                }

                var within = string.IsNullOrEmpty(normalizedWithin) ? name : $"{normalizedWithin}/{name}";
                if (IsInPycacheFolder(within))
                {
                    continue;
                }

                var fullRelative = string.IsNullOrEmpty(prefix) ? within : $"{prefix}/{within}";
                directories.Add(new ScannedDirectory(fullRelative, name));
            }

            foreach (var physicalFile in Directory.EnumerateFiles(targetPhysical))
            {
                if (DateTimeOffset.UtcNow >= scanDeadlineUtc || files.Count >= maxFiles)
                {
                    truncated = true;
                    break;
                }

                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(physicalFile);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                var fileName = fileInfo.Name;
                if (IsTemporaryScriptFile(fileName))
                {
                    continue;
                }

                var within = string.IsNullOrEmpty(normalizedWithin) ? fileName : $"{normalizedWithin}/{fileName}";
                if (IsInPycacheFolder(within))
                {
                    continue;
                }

                var fullRelative = string.IsNullOrEmpty(prefix) ? within : $"{prefix}/{within}";
                files.Add(new ScannedFile(
                    RelativePath: fullRelative,
                    FileName: fileName,
                    FileSize: fileInfo.Length,
                    LastModifiedUtc: fileInfo.LastWriteTimeUtc));
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Skipping mount one-level listing for root {MountRoot} path {RelativePath}",
                prefix,
                normalizedWithin);
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        return new ScanResult(files, directories, truncated);
    }

    private static bool IsExcludedDirectoryName(string directoryName) =>
        string.Equals(directoryName, "__pycache__", StringComparison.OrdinalIgnoreCase)
        || string.Equals(directoryName, ".guideants", StringComparison.OrdinalIgnoreCase)
        || string.Equals(directoryName, "node_modules", StringComparison.OrdinalIgnoreCase)
        || string.Equals(directoryName, ".git", StringComparison.OrdinalIgnoreCase);

    private static bool IsTemporaryScriptFile(string filename)
    {
        var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
        return Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase);
    }

    private static bool IsInPycacheFolder(string relativePath)
    {
        return relativePath.StartsWith("__pycache__/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Contains("/__pycache__/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.EndsWith("/__pycache__", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim('/');

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
        return candidateFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
