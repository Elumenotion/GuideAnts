namespace GuideAntsApi.Services.Components.Sync;

/// <summary>
/// Paths under these directory names are tooling caches/artifacts — not user-facing notebook content.
/// They are excluded from sync indexing and from <c>[@files]</c> context resolution.
/// </summary>
public static class NotebookArtifactPathExclusions
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".npm",
        "node_modules",
        "__pycache__",
        ".git",
        ".pytest_cache",
        "_cacache",
        ".cache",
        ".venv",
        "venv",
        ".tox",
        ".mypy_cache",
        ".ruff_cache",
        // Skill/runtime working state under the notebook CWD (not user content).
        ".audiocpp-extended",
        ".wire-attachments",
        // Local model / timestamp weight caches dropped under Output/ by some skills.
        "models-timestamps",
    };

    public static bool IsExcludedDirectorySegment(string directoryName) =>
        !string.IsNullOrWhiteSpace(directoryName) && ExcludedDirectoryNames.Contains(directoryName);

    public static bool IsExcludedRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExcludedDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }
}
