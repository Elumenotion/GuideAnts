namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Explicit toolset-to-tool mapping used for offer-time skill gating (S6/S9).
/// </summary>
public static class SkillToolsetMapping
{
        private static readonly Dictionary<string, string[]> ToolsetToTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sandbox"] = ["code_interpreter", "run_python", "run_bash"],
            ["terminal"] = ["code_interpreter", "run_python", "run_bash"],
            ["web"] = ["WebSearch", "ReadWeb"],
        };

    public static IReadOnlyDictionary<string, string[]> All => ToolsetToTools;

    public static bool IsToolsetAvailable(string toolset, IReadOnlySet<string> availableTools)
    {
        if (!ToolsetToTools.TryGetValue(toolset, out var mappedTools))
        {
            return false;
        }

        return mappedTools.Any(availableTools.Contains);
    }
}
