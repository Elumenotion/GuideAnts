using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Components;

public static class HostMountDirectoryScanner
{
    public sealed record ScannedFile(
        string RelativePath,
        string FileName,
        long FileSize,
        DateTime LastModifiedUtc);

    public sealed record ScanResult(
        IReadOnlyList<ScannedFile> Files,
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
        var results = new List<ScannedFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var scanDeadlineUtc = DateTimeOffset.UtcNow + scanBudget;
        var fileEnumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MaxRecursionDepth = maxDepth
        };

        foreach (var root in roots)
        {
            if (DateTimeOffset.UtcNow >= scanDeadlineUtc || results.Count >= maxFiles)
            {
                truncated = true;
                break;
            }

            if (string.IsNullOrWhiteSpace(root.PhysicalPath) || !Directory.Exists(root.PhysicalPath))
            {
                continue;
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
                if (DateTimeOffset.UtcNow >= scanDeadlineUtc || results.Count >= maxFiles)
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

                if (!seenPaths.Add(fullRelativePath))
                {
                    continue;
                }

                results.Add(new ScannedFile(
                    RelativePath: fullRelativePath,
                    FileName: fileName,
                    FileSize: fileInfo.Length,
                    LastModifiedUtc: fileInfo.LastWriteTimeUtc));
            }
        }

        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        return new ScanResult(results, truncated);
    }

    private static bool IsTemporaryScriptFile(string filename)
    {
        var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
        return Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase);
    }

    private static bool IsInPycacheFolder(string relativePath)
    {
        return relativePath.StartsWith("__pycache__/") ||
               relativePath.Contains("/__pycache__/") ||
               relativePath.EndsWith("/__pycache__");
    }
}
