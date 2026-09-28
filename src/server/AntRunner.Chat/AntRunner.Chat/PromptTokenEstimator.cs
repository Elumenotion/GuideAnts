using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat;

/// <summary>
/// Estimates prompt size from character counts. Provider-reported usage is always preferred;
/// this exists for the cases where none has been observed yet, and to project the small
/// amount of content added since the last provider-reported round.
/// </summary>
public static class PromptTokenEstimator
{
    public const double DefaultCharsPerToken = 4.0;

    private const double MinCharsPerToken = 1.5;
    private const double MaxCharsPerToken = 8.0;

    /// <summary>
    /// Counts the characters a provider would tokenize: text content, tool-call names and
    /// tool-call arguments. Image content counts as zero — its token cost is provider-specific.
    /// </summary>
    public static int CountChars(IEnumerable<ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Content ?? [])
            {
                total += content.Text?.Length ?? 0;
            }

            if (message.ToolCalls != null)
            {
                foreach (var call in message.ToolCalls)
                {
                    total += call.Function.Name?.Length ?? 0;
                    total += ArgumentsLength(call.Function.Arguments);
                }
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    // Arguments is a JsonElement: either a JSON string holding the payload, or the payload itself.
    private static int ArgumentsLength(JsonElement arguments) => arguments.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => 0,
        JsonValueKind.String => arguments.GetString()?.Length ?? 0,
        _ => arguments.GetRawText().Length
    };

    public static int EstimateTokens(int chars, double charsPerToken = DefaultCharsPerToken)
    {
        if (chars <= 0)
        {
            return 0;
        }

        if (double.IsNaN(charsPerToken) || charsPerToken <= 0)
        {
            charsPerToken = DefaultCharsPerToken;
        }

        return (int)Math.Ceiling(chars / charsPerToken);
    }

    /// <summary>
    /// Learns chars-per-token from turns where the provider reported real prompt tokens.
    /// Pooled (sum chars / sum tokens) so one tiny turn cannot dominate; clamped so a bad
    /// observation cannot produce an absurd meter.
    /// </summary>
    public static double CharsPerToken(IEnumerable<(int Tokens, int Chars)> observations)
    {
        long tokens = 0;
        long chars = 0;
        foreach (var (t, c) in observations)
        {
            if (t <= 0 || c <= 0)
            {
                continue;
            }

            tokens += t;
            chars += c;
        }

        if (tokens == 0)
        {
            return DefaultCharsPerToken;
        }

        return Math.Clamp((double)chars / tokens, MinCharsPerToken, MaxCharsPerToken);
    }
}
