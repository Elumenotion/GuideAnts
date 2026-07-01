namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Parses and formats skill locators (S7): internal skill://assistantId/name
/// and published skill://guide/name (+ optional /references/path).
/// </summary>
public static class SkillLocator
{
    private const string SchemePrefix = "skill://";

    public static string FormatInternal(Guid assistantId, string skillName) =>
        $"{SchemePrefix}{assistantId}/{skillName}";

    public static string FormatPublished(string guideName, string skillName) =>
        $"{SchemePrefix}{guideName}/{skillName}";

    public static string FormatPublishedReference(string guideName, string skillName, string referencePath)
    {
        var normalized = SkillPathSafety.NormalizePath(referencePath);
        return $"{SchemePrefix}{guideName}/{skillName}/references/{normalized}";
    }

    public static bool TryParse(string locator, out SkillLocatorParts parts)
    {
        parts = default;

        if (string.IsNullOrWhiteSpace(locator)
            || !locator.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = locator[SchemePrefix.Length..];
        var slashIndex = remainder.IndexOf('/');
        if (slashIndex <= 0)
        {
            return false;
        }

        var firstSegment = remainder[..slashIndex];
        var afterFirst = remainder[(slashIndex + 1)..];
        if (string.IsNullOrWhiteSpace(afterFirst))
        {
            return false;
        }

        if (Guid.TryParse(firstSegment, out var assistantId))
        {
            if (afterFirst.Contains('/'))
            {
                return false;
            }

            parts = new SkillLocatorParts(
                IsPublished: false,
                GuideName: null,
                AssistantId: assistantId,
                SkillName: afterFirst,
                ReferenceRelativePath: null);
            return !string.IsNullOrWhiteSpace(parts.SkillName);
        }

        var segments = afterFirst.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var skillName = segments[0];
        string? referencePath = null;

        if (segments.Length > 1)
        {
            if (segments.Length < 3
                || !segments[1].Equals("references", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            referencePath = string.Join('/', segments.Skip(2));
            if (string.IsNullOrWhiteSpace(referencePath)
                || referencePath.Split('/').Any(segment => segment == ".."))
            {
                return false;
            }
        }

        parts = new SkillLocatorParts(
            IsPublished: true,
            GuideName: firstSegment,
            AssistantId: null,
            SkillName: skillName,
            ReferenceRelativePath: referencePath);
        return !string.IsNullOrWhiteSpace(skillName);
    }
}

public readonly record struct SkillLocatorParts(
    bool IsPublished,
    string? GuideName,
    Guid? AssistantId,
    string SkillName,
    string? ReferenceRelativePath);
