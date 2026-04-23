namespace GuideAntsApi.DataModel.Utilities;

public static class StoragePathCompatibility
{
    private static readonly string[] RootAnchors = ["projects", "assistants"];

    public static bool TryResolveExistingFilePath(
        string? storedPath,
        string? storageRoot,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        foreach (var candidate in GetCandidatePaths(storedPath, storageRoot))
        {
            if (File.Exists(candidate))
            {
                resolvedPath = candidate;
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> GetCandidatePaths(string? storedPath, string? storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedStored = NormalizeSeparators(storedPath);

        foreach (var candidate in EnumerateCandidates(normalizedStored, storageRoot))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalizedCandidate = NormalizeSeparators(candidate);
            if (emitted.Add(normalizedCandidate))
            {
                yield return normalizedCandidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidates(string normalizedStoredPath, string? storageRoot)
    {
        yield return normalizedStoredPath;

        if (TryGetFullPath(normalizedStoredPath, out var fullStored))
        {
            yield return fullStored;
        }

        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            yield break;
        }

        var normalizedRoot = NormalizeSeparators(storageRoot);
        if (!TryGetFullPath(normalizedRoot, out var rootFullPath))
        {
            rootFullPath = normalizedRoot;
        }

        var relativeWithoutLeadingTraversal = TrimLeadingTraversalSegments(normalizedStoredPath);
        if (!string.IsNullOrWhiteSpace(relativeWithoutLeadingTraversal))
        {
            yield return Path.Combine(rootFullPath, relativeWithoutLeadingTraversal);
        }

        var tokens = Tokenize(normalizedStoredPath);
        if (tokens.Count == 0)
        {
            yield break;
        }

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootFullPath));
        if (!string.IsNullOrWhiteSpace(rootName))
        {
            var rootNameIndex = IndexOfToken(tokens, rootName);
            if (rootNameIndex >= 0 && rootNameIndex + 1 < tokens.Count)
            {
                yield return Path.Combine(rootFullPath, Path.Combine(tokens.Skip(rootNameIndex + 1).ToArray()));
            }
        }

        foreach (var anchor in RootAnchors)
        {
            var anchorIndex = IndexOfToken(tokens, anchor);
            if (anchorIndex >= 0)
            {
                yield return Path.Combine(rootFullPath, Path.Combine(tokens.Skip(anchorIndex).ToArray()));
                foreach (var migratedCandidate in EnumerateMigratedAnchorCandidates(tokens, anchorIndex, rootFullPath, anchor))
                {
                    yield return migratedCandidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateMigratedAnchorCandidates(
        IReadOnlyList<string> tokens,
        int anchorIndex,
        string rootFullPath,
        string anchor)
    {
        // Handle migrated project shadow paths:
        // legacy: <root>/projects/{projectGuid}/...
        // named : <root>/projects/{projectSlug}/...
        if (!string.Equals(anchor, "projects", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        if (anchorIndex + 2 >= tokens.Count)
        {
            yield break;
        }

        var projectToken = tokens[anchorIndex + 1];
        var projectsRoot = Path.Combine(rootFullPath, "projects");
        if (!Directory.Exists(projectsRoot))
        {
            yield break;
        }

        var suffixTokens = tokens.Skip(anchorIndex + 2).ToArray();
        var isLegacyGuidProjectToken = Guid.TryParse(projectToken, out _);
        foreach (var projectDir in Directory.EnumerateDirectories(projectsRoot))
        {
            var slug = Path.GetFileName(projectDir);
            if (string.Equals(slug, projectToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Always try alternate project folders when token no longer matches current slug.
            // For GUID->slug migrations this preserves existing behavior.
            // For slug->slug normalizations (e.g. ASCII-safe renames), this also repairs stale DB paths.
            if (!isLegacyGuidProjectToken && !Directory.Exists(Path.Combine(projectsRoot, projectToken)))
            {
                // proceed
            }
            else if (!isLegacyGuidProjectToken)
            {
                // If the token still exists as a real folder, prefer direct resolution and skip broad fallback.
                continue;
            }

            if (suffixTokens.Length == 0)
            {
                yield return projectDir;
            }
            else
            {
                yield return Path.Combine(projectDir, Path.Combine(suffixTokens));

                // Handle migrated notebook markdown paths:
                // legacy: projects/{projectGuid}/notebooks/{notebookGuid}/...
                // named : projects/{projectSlug}/{notebookSlug}/...
                if (suffixTokens.Length >= 3 &&
                    string.Equals(suffixTokens[0], "notebooks", StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(suffixTokens[1], out _))
                {
                    var trailing = suffixTokens.Skip(2).ToArray();
                    foreach (var notebookDir in Directory.EnumerateDirectories(projectDir))
                    {
                        var notebookName = Path.GetFileName(notebookDir);
                        if (string.Equals(notebookName, "content", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(notebookName, "notebooks", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(notebookName, "assistants", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (trailing.Length == 0)
                        {
                            yield return notebookDir;
                        }
                        else
                        {
                            yield return Path.Combine(notebookDir, Path.Combine(trailing));
                        }
                    }
                }
            }
        }
    }

    private static string NormalizeSeparators(string path)
    {
        var slashNormalized = path.Replace('\\', '/');
        return Path.DirectorySeparatorChar == '/'
            ? slashNormalized
            : slashNormalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string TrimLeadingTraversalSegments(string path)
    {
        var trimmed = path;
        var parentPrefix = $"..{Path.DirectorySeparatorChar}";
        var currentPrefix = $".{Path.DirectorySeparatorChar}";

        while (trimmed.StartsWith(parentPrefix, StringComparison.Ordinal) ||
               trimmed.StartsWith(currentPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed.StartsWith(parentPrefix, StringComparison.Ordinal)
                ? trimmed[parentPrefix.Length..]
                : trimmed[currentPrefix.Length..];
        }

        return trimmed.TrimStart(Path.DirectorySeparatorChar);
    }

    private static List<string> Tokenize(string path) =>
        path.Split([Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    private static int IndexOfToken(IReadOnlyList<string> tokens, string token)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            fullPath = path;
            return false;
        }
    }
}
