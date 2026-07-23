using GuideAntsApi.DataModel.Models;

namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Maps skill <c>scripts/</c> and <c>assets/</c> payload files onto notebook
/// <c>Resources/</c> and <c>Output/</c> paths (same materialization model as CodeInterpreter files).
/// <c>SKILL.md</c> and <c>references/</c> stay on-demand via <c>skills_read</c>.
/// </summary>
public static class SkillNotebookMaterializer
{
    public static bool IsMaterializablePayloadPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = SkillPathSafety.NormalizePath(relativePath);
        if (!normalized.StartsWith("Skills/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = normalized.Split('/');
        if (parts.Length < 4)
        {
            return false;
        }

        var payloadFolder = parts[2];
        return payloadFolder.Equals("scripts", StringComparison.OrdinalIgnoreCase)
               || payloadFolder.Equals("assets", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AssistantHasMaterializableSkillPayload(IEnumerable<AssistantFile> files) =>
        files.Any(f =>
            string.Equals(f.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)
            && IsMaterializablePayloadPath(f.RelativePath));

    /// <summary>
    /// Maps <c>Skills/&lt;name&gt;/scripts/...</c> to
    /// <c>Resources/Skills/&lt;name&gt;/scripts/...</c> (guide) or
    /// <c>Resources/crew-&lt;crew&gt;/Skills/&lt;name&gt;/scripts/...</c> (crew).
    /// </summary>
    public static string ToNotebookResourcePath(string assistantRelativePath, string? crewSafeName)
    {
        if (!IsMaterializablePayloadPath(assistantRelativePath))
        {
            throw new ArgumentException(
                $"Skill file '{assistantRelativePath}' is not a materializable scripts/assets payload path.",
                nameof(assistantRelativePath));
        }

        var normalized = SkillPathSafety.NormalizePath(assistantRelativePath);
        if (string.IsNullOrWhiteSpace(crewSafeName))
        {
            return $"Resources/{normalized}";
        }

        return $"Resources/crew-{crewSafeName}/{normalized}";
    }

    /// <summary>
    /// Maps a notebook resource path to its projected <c>Output/</c> symlink path.
    /// </summary>
    public static string ToOutputProjectionPath(string notebookResourcePath)
    {
        var normalized = SkillPathSafety.NormalizePath(notebookResourcePath);
        if (!normalized.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Notebook resource path must start with Resources/: '{notebookResourcePath}'",
                nameof(notebookResourcePath));
        }

        var afterResources = normalized["Resources/".Length..];
        return $"Output/{afterResources}";
    }

    /// <summary>
    /// Relative symlink target from an <c>Output/...</c> file to its <c>Resources/...</c> backing file.
    /// </summary>
    public static string SymlinkTargetFromOutputFile(string outputRelativePath, string notebookResourcePath)
    {
        var normalizedOutput = SkillPathSafety.NormalizePath(outputRelativePath);
        if (!normalizedOutput.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Output projection path must start with Output/: '{outputRelativePath}'",
                nameof(outputRelativePath));
        }

        var depth = normalizedOutput["Output/".Length..].Split('/').Length;
        return string.Concat(Enumerable.Repeat("../", depth))
               + SkillPathSafety.NormalizePath(notebookResourcePath);
    }
}
