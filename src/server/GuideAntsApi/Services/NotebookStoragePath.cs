namespace GuideAntsApi.Services;

internal static class NotebookStoragePath
{
    public static bool TryResolveUnderRoot(string rootDirectory, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Empty relative path means "the root itself" for callers that operate on notebook root.
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            fullPath = root;
            return true;
        }

        if (relativePath.IndexOf('\0') >= 0 || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var combined = Path.GetFullPath(Path.Combine(root, normalizedRelativePath))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!IsStrictChildOrSamePath(root, combined))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }

    public static bool TryResolveDirectoryUnderRoot(string rootDirectory, string? relativePath, out string fullPath)
        => TryResolveUnderRoot(rootDirectory, relativePath, out fullPath);

    public static string? SanitizeFileName(string fileName)
    {
        var sanitized = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
        {
            return null;
        }

        return sanitized;
    }

    private static bool IsStrictChildOrSamePath(string root, string path)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
