using OpenAI;

namespace AntRunner.Chat.OpenAI;

internal static class OpenAiReasoningSupport
{
    internal static bool SupportsReasoningEffort(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalized = model.Trim();
        return normalized.StartsWith("o", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
    }

    internal static ReasoningEffort? MapReasoningEffort(string? model, string? effort)
    {
        if (!SupportsReasoningEffort(model) || string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "minimal" => ReasoningEffort.Minimal,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            _ => null
        };
    }
}
