using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Parsed YAML frontmatter from a <c>SKILL.md</c> file.
/// Accepts agentskills.io, hermes <c>metadata.hermes.*</c>, and Claude-Code extension fields.
/// </summary>
public sealed class SkillFrontmatter
{
    public const int MaxSkillMarkdownChars = 100_000;

    /// <summary>Recommended skill description length per the agentskills.io specification.</summary>
    public const int RecommendedDescriptionLength = 60;

    /// <summary>Hard database limit for <see cref="Description"/>.</summary>
    public const int MaxDescriptionLength = 1024;

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int DisplayOrder { get; init; }
    public List<string> RequiresToolsets { get; init; } = [];
    public List<string> RequiresTools { get; init; } = [];
    public List<string> FallbackForToolsets { get; init; } = [];
    public List<string> FallbackForTools { get; init; } = [];
    public List<string> Platforms { get; init; } = [];

    /// <summary>
    /// Parses frontmatter from a full <c>SKILL.md</c> document. Throws on missing required fields.
    /// </summary>
    public static SkillFrontmatter Parse(string skillMarkdown)
    {
        if (string.IsNullOrEmpty(skillMarkdown))
        {
            throw new InvalidOperationException("SKILL.md content is empty.");
        }

        if (skillMarkdown.Length > MaxSkillMarkdownChars)
        {
            throw new InvalidOperationException(
                $"SKILL.md exceeds maximum length of {MaxSkillMarkdownChars:N0} characters.");
        }

        var yaml = ExtractFrontmatterYaml(skillMarkdown);
        Dictionary<string, object?> root;
        try
        {
            root = DeserializeYaml(yaml);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException("SKILL.md frontmatter is not valid YAML.", ex);
        }

        var name = GetString(root, "name")
                   ?? GetMetadataString(root, "guideants", "name")
                   ?? GetMetadataString(root, "hermes", "name");
        var description = GetString(root, "description")
                          ?? GetMetadataString(root, "guideants", "description")
                          ?? GetMetadataString(root, "hermes", "description");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("SKILL.md frontmatter is missing required field 'name'.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("SKILL.md frontmatter is missing required field 'description'.");
        }

        var normalizedDescription = NormalizeDescription(description);

        var guideants = GetMetadataSection(root, "guideants");
        var hermes = GetMetadataSection(root, "hermes");

        var enabled = GetBool(guideants, "enabled")
                      ?? GetBool(hermes, "enabled")
                      ?? true;

        var displayOrder = GetInt(guideants, "display_order")
                           ?? GetInt(hermes, "display_order")
                           ?? 0;

        var requiresToolsets = CoalesceLists(
            GetStringList(guideants, "requires_toolsets"),
            GetStringList(hermes, "requires_toolsets"));
        var requiresTools = CoalesceLists(
            GetStringList(guideants, "requires_tools"),
            GetStringList(hermes, "requires_tools"),
            GetStringList(root, "allowed-tools"));
        var fallbackForToolsets = CoalesceLists(
            GetStringList(guideants, "fallback_for_toolsets"),
            GetStringList(hermes, "fallback_for_toolsets"));
        var fallbackForTools = CoalesceLists(
            GetStringList(guideants, "fallback_for_tools"),
            GetStringList(hermes, "fallback_for_tools"));
        var platforms = GetStringList(root, "platforms");

        // Claude-Code fields (allowed-tools, argument-hint) are tolerated above; argument-hint is ignored.

        return new SkillFrontmatter
        {
            Name = name.Trim(),
            Description = normalizedDescription,
            Enabled = enabled,
            DisplayOrder = displayOrder,
            RequiresToolsets = requiresToolsets,
            RequiresTools = requiresTools,
            FallbackForToolsets = fallbackForToolsets,
            FallbackForTools = fallbackForTools,
            Platforms = platforms,
        };
    }

    /// <summary>
    /// Trims and truncates a skill description to <see cref="MaxDescriptionLength"/>.
    /// </summary>
    public static string NormalizeDescription(string description)
    {
        var trimmed = description.Trim();
        return trimmed.Length <= MaxDescriptionLength
            ? trimmed
            : trimmed[..MaxDescriptionLength];
    }

    /// <summary>
    /// Returns markdown body text after the closing frontmatter delimiter.
    /// </summary>
    public static string ExtractBody(string skillMarkdown)
    {
        if (string.IsNullOrEmpty(skillMarkdown))
        {
            return string.Empty;
        }

        if (!skillMarkdown.StartsWith("---", StringComparison.Ordinal))
        {
            return skillMarkdown;
        }

        var closeIndex = skillMarkdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            return skillMarkdown;
        }

        var bodyStart = closeIndex + "\n---".Length;
        while (bodyStart < skillMarkdown.Length && (skillMarkdown[bodyStart] == '\r' || skillMarkdown[bodyStart] == '\n'))
        {
            bodyStart++;
        }

        return skillMarkdown[bodyStart..];
    }

    private static string ExtractFrontmatterYaml(string skillMarkdown)
    {
        if (!skillMarkdown.StartsWith("---", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SKILL.md is missing YAML frontmatter delimiters (---).");
        }

        var closeIndex = skillMarkdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            throw new InvalidOperationException("SKILL.md frontmatter is not closed with a second --- delimiter.");
        }

        return skillMarkdown[3..closeIndex].Trim('\r', '\n', ' ');
    }

    private static Dictionary<string, object?> DeserializeYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parsed = deserializer.Deserialize<Dictionary<string, object?>>(yaml);
        return parsed ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetString(Dictionary<string, object?> root, string key)
    {
        if (!root.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            _ => value.ToString(),
        };
    }

    private static Dictionary<string, object?>? GetMetadataSection(Dictionary<string, object?> root, string section)
    {
        if (!root.TryGetValue("metadata", out var metadata) || metadata == null)
        {
            return null;
        }

        if (metadata is Dictionary<string, object?> dict &&
            dict.TryGetValue(section, out var sectionValue) &&
            sectionValue is Dictionary<string, object?> sectionDict)
        {
            return sectionDict;
        }

        if (metadata is IDictionary<object, object?> genericDict &&
            genericDict.TryGetValue(section, out var sectionObj) &&
            sectionObj is IDictionary<object, object?> nested)
        {
            return nested.ToDictionary(
                kvp => kvp.Key.ToString() ?? "",
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        return null;
    }

    private static string? GetMetadataString(Dictionary<string, object?> root, string section, string key)
    {
        var sectionDict = GetMetadataSection(root, section);
        return sectionDict == null ? null : GetString(sectionDict, key);
    }

    private static bool? GetBool(Dictionary<string, object?>? section, string key)
    {
        if (section == null || !section.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? GetInt(Dictionary<string, object?>? section, string key)
    {
        if (section == null || !section.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    private static List<string> GetStringList(Dictionary<string, object?>? section, string key)
    {
        if (section == null || !section.TryGetValue(key, out var value) || value == null)
        {
            return [];
        }

        return ToStringList(value);
    }

    private static List<string> ToStringList(object value)
    {
        if (value is string single)
        {
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        if (value is IEnumerable<object?> sequence)
        {
            return sequence
                .Select(item => item?.ToString()?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
        }

        return [];
    }

    private static List<string> CoalesceLists(params List<string>[] lists)
    {
        foreach (var list in lists)
        {
            if (list.Count > 0)
            {
                return list;
            }
        }

        return [];
    }
}
