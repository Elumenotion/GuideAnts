using System.Text;
using System.Text.RegularExpressions;

namespace AntRunner.ToolCalling;

/// <summary>
/// Normalizes tool operation IDs to the cross-provider wire format
/// <c>^[a-zA-Z0-9_-]{1,64}$</c> required by OpenAI, Anthropic, Gemini, and similar APIs.
/// </summary>
public static partial class ToolOperationIdSanitizer
{
    public const string WireNamePattern = @"^[a-zA-Z0-9_-]{1,64}$";

    [GeneratedRegex(WireNamePattern)]
    private static partial Regex WireNameRegex();

    public static bool IsWireCompatible(string? operationId) =>
        !string.IsNullOrWhiteSpace(operationId) && WireNameRegex().IsMatch(operationId);

    /// <summary>
    /// Returns a provider-safe tool name. Invalid characters become underscores; result is trimmed
    /// and capped at 64 characters.
    /// </summary>
    public static string ToWireName(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return "tool";
        }

        var sb = new StringBuilder(operationId.Length);
        foreach (var ch in operationId)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        var result = sb.ToString().Trim('_', '-');
        if (string.IsNullOrEmpty(result))
        {
            result = "tool";
        }

        if (result.Length > 64)
        {
            result = result[..64];
        }

        return result;
    }

    /// <summary>
    /// Reserves a unique wire name when multiple operations may collide after sanitization.
    /// </summary>
    public static string ToUniqueWireName(string? operationId, ISet<string> used)
    {
        var baseId = ToWireName(operationId);
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
