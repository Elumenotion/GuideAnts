namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Canonicalizes and validates paths under a <c>Skills/&lt;name&gt;/</c> root.
/// </summary>
public static class SkillPathSafety
{
    public static string ResolveUnderSkillFolder(string folderPath, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidOperationException("Skill folder path is required.");
        }

        var normalizedFolder = NormalizePath(folderPath);
        if (!normalizedFolder.StartsWith("Skills/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid skill folder path '{folderPath}'.");
        }

        var relative = string.IsNullOrWhiteSpace(filePath) ? "SKILL.md" : filePath;
        if (relative.StartsWith('/') || relative.StartsWith('\\') || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Absolute paths are not allowed for skill file reads.");
        }

        relative = NormalizePath(relative);

        if (relative.Split('/').Any(segment => segment == ".."))
        {
            throw new InvalidOperationException("Path traversal ('..') is not allowed for skill file reads.");
        }

        var combined = NormalizePath($"{normalizedFolder}/{relative}");
        if (!combined.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, normalizedFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resolved path '{combined}' escapes the skill folder '{normalizedFolder}'.");
        }

        return combined;
    }

    public static string? SkillFolderKey(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var parts = NormalizePath(relativePath).Split('/');
        return parts.Length >= 2 && parts[0].Equals("Skills", StringComparison.OrdinalIgnoreCase)
            ? $"{parts[0]}/{parts[1]}"
            : null;
    }

    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim('/');
}
