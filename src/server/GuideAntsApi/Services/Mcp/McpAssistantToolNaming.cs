using System.Text;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Sanitizes assistant display names into MCP tool names (aligned with crew-bridge operation IDs).
/// </summary>
public static class McpAssistantToolNaming
{
    public static IReadOnlyList<McpAddressableAssistant> AssignToolNames(
        IEnumerable<(Guid Id, string Name, string Description, bool IsGuide)> assistants)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<McpAddressableAssistant>();

        foreach (var (id, name, description, isGuide) in assistants)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var toolName = SanitizeOperationId(name.Trim(), usedIds);
            var desc = string.IsNullOrWhiteSpace(description)
                ? $"Invoke {name.Trim()}."
                : description.Trim();

            result.Add(new McpAddressableAssistant(id, name.Trim(), toolName, desc, isGuide));
        }

        return result;
    }

    public static string SanitizeOperationId(string name, ISet<string> used)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        var baseId = sb.ToString().Trim('_', '-');
        if (string.IsNullOrEmpty(baseId))
            baseId = "assistant";

        if (baseId.Length > 64)
            baseId = baseId[..64];

        var candidate = baseId;
        var i = 2;
        while (used.Contains(candidate))
        {
            var suffix = "_" + i.ToString();
            var limit = 64 - suffix.Length;
            candidate = (baseId.Length > limit ? baseId[..limit] : baseId) + suffix;
            i++;
        }

        used.Add(candidate);
        return candidate;
    }
}
