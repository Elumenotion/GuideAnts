using AntRunner.Chat;

namespace GuideAntsApi.Services.Components.Sync;

public static class NotebookPathResolver
{
    public static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    public static string ToCwdRelative(string dbRelativePath, bool isPublished, string? runId) =>
        NotebookFileChangeReporter.ToCwdRelativePath(dbRelativePath, isPublished, runId);

    public static string ToDbRelative(string cwdRelativePath, bool isPublished, string? runId)
    {
        if (string.IsNullOrWhiteSpace(cwdRelativePath))
        {
            return cwdRelativePath;
        }

        var normalized = NormalizeRelativePath(cwdRelativePath);

        if (normalized.StartsWith("Output/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Runs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith("../", StringComparison.Ordinal))
        {
            var cwdBase = isPublished && !string.IsNullOrWhiteSpace(runId)
                ? $"Runs/{runId}"
                : "Output";
            return ResolveRelativePath(cwdBase, normalized);
        }

        if (isPublished && !string.IsNullOrWhiteSpace(runId))
        {
            return $"Runs/{runId}/{normalized}";
        }

        return $"Output/{normalized}";
    }

    public static IReadOnlyList<string> GetDbRelativePaths(ChatRunOutput? output, bool isPublished, string? runId)
    {
        if (output == null)
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cwdPath in output.NewFiles ?? [])
        {
            if (!string.IsNullOrWhiteSpace(cwdPath))
            {
                paths.Add(ToDbRelative(cwdPath, isPublished, runId));
            }
        }

        foreach (var cwdPath in output.ModifiedFiles ?? [])
        {
            if (!string.IsNullOrWhiteSpace(cwdPath))
            {
                paths.Add(ToDbRelative(cwdPath, isPublished, runId));
            }
        }

        return paths.ToList();
    }

    public static IEnumerable<string> GetAlternativePaths(string originalPath)
    {
        var alternatives = new List<string>();
        var workingDirs = new[] { "Output", "Runs" };

        if (originalPath.StartsWith("../", StringComparison.Ordinal))
        {
            foreach (var workDir in workingDirs)
            {
                var resolved = ResolveRelativePath(workDir, originalPath);
                if (!string.IsNullOrEmpty(resolved) && resolved != originalPath)
                {
                    alternatives.Add(resolved);
                }
            }

            var withoutParent = originalPath;
            while (withoutParent.StartsWith("../", StringComparison.Ordinal))
            {
                withoutParent = withoutParent[3..];
            }

            if (!string.IsNullOrEmpty(withoutParent) && withoutParent != originalPath)
            {
                alternatives.Add(withoutParent);
            }
        }
        else if (!originalPath.Contains('/') || !originalPath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var workDir in workingDirs)
            {
                alternatives.Add($"{workDir}/{originalPath}");
            }
        }

        return alternatives.Distinct();
    }

    public static string? TryExtractRunIdFromWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var normalized = NormalizeRelativePath(workingDirectory);
        const string prefix = "Runs/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remainder = normalized[prefix.Length..];
        var slash = remainder.IndexOf('/');
        return slash < 0 ? remainder : remainder[..slash];
    }

    private static string ResolveRelativePath(string baseDir, string relativePath)
    {
        var baseParts = baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var pathParts = relativePath.Split('/').ToList();

        foreach (var segment in pathParts)
        {
            if (segment == "..")
            {
                if (baseParts.Count > 0)
                {
                    baseParts.RemoveAt(baseParts.Count - 1);
                }
            }
            else if (segment != "." && !string.IsNullOrEmpty(segment))
            {
                baseParts.Add(segment);
            }
        }

        return string.Join("/", baseParts);
    }
}
