using System.Text;
using System.Text.Json;

namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Provider-agnostic detection of "prompt exceeds the model context window" errors. Every chat
/// provider reports this differently (llama.cpp <c>exceed_context_size_error</c>, OpenAI
/// <c>context_length_exceeded</c>, Anthropic "prompt is too long", Gemini token-count messages),
/// so the marker knowledge lives here once instead of being duplicated per client. Raw-HTTP clients
/// classify from the response body; the execution engine classifies thrown exceptions from SDK-based
/// clients. Both paths converge on <see cref="ChatContextOverflowException"/>.
/// </summary>
public static class ChatContextOverflowClassifier
{
    // Specific phrases only — broad words like "context window" are intentionally excluded so a
    // generic bad request is never misclassified as an overflow.
    private static readonly string[] Markers =
    [
        // llama.cpp / llama-server
        "exceed_context_size_error",
        "exceeds the available context size",
        "exceeds the available context",
        // OpenAI / Azure OpenAI
        "context_length_exceeded",
        "maximum context length",
        "reduce the length of the messages",
        // Anthropic
        "prompt is too long",
        "exceed context limit",
        "input length and max_tokens exceed context limit",
        // Google Gemini
        "exceeds the maximum number of tokens",
        "input token count",
        "the input token count",
    ];

    /// <summary>
    /// Classifies a raw provider error body. Returns true only for 4xx-class responses whose body
    /// matches a known overflow marker. Best-effort token counts are extracted when present.
    /// </summary>
    public static bool TryClassifyBody(int? statusCode, string? body, out int? promptTokens, out int? contextSize)
    {
        promptTokens = null;
        contextSize = null;

        // Context overflow is always a client-side request-shaping rejection (4xx). 5xx is a crash.
        if (statusCode is < 400 or >= 500)
        {
            return false;
        }

        if (!ContainsMarker(body))
        {
            return false;
        }

        TryExtractTokenCounts(body, out promptTokens, out contextSize);
        return true;
    }

    /// <summary>
    /// Classifies an exception thrown by a provider/SDK by scanning its message chain for a known
    /// overflow marker. Used by the engine as the single normalization point across all providers.
    /// </summary>
    public static bool Matches(Exception? exception)
    {
        if (exception == null)
        {
            return false;
        }

        return ContainsMarker(CollectMessages(exception));
    }

    /// <summary>Short, user-safe excerpt of the provider error for diagnostics.</summary>
    public static string? Excerpt(Exception? exception)
    {
        if (exception == null)
        {
            return null;
        }

        var message = exception.Message?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        const int maxLength = 500;
        return message.Length <= maxLength ? message : message[..maxLength] + "…";
    }

    private static bool ContainsMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var marker in Markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CollectMessages(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var depth = 0;
        while (current != null && depth < 10)
        {
            // Message is the common case, but provider SDKs vary: some surface the upstream error
            // body only in a property (e.g. ResponseBody/Content) or in Data rather than Message.
            // Append the message plus those well-known carriers so detection is robust to unknown
            // exception shapes without coupling to any specific SDK type.
            builder.Append(current.Message);
            builder.Append('\n');
            AppendKnownBodyCarriers(current, builder);
            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }

    private static readonly string[] BodyCarrierPropertyNames =
    [
        "ResponseBody",
        "Content",
        "Body",
        "ResponseContent",
        "ResponseMessage",
        "Error",
    ];

    private static void AppendKnownBodyCarriers(Exception exception, StringBuilder builder)
    {
        foreach (var propertyName in BodyCarrierPropertyNames)
        {
            try
            {
                var property = exception.GetType().GetProperty(propertyName);
                if (property?.GetValue(exception) is string value && !string.IsNullOrEmpty(value))
                {
                    builder.Append(value);
                    builder.Append('\n');
                }
            }
            catch
            {
                // Reflection over an arbitrary SDK exception is best-effort; ignore property failures.
            }
        }

        foreach (var entry in exception.Data.Values)
        {
            if (entry is string value && !string.IsNullOrEmpty(value))
            {
                builder.Append(value);
                builder.Append('\n');
            }
        }
    }

    private static void TryExtractTokenCounts(string? body, out int? promptTokens, out int? contextSize)
    {
        promptTokens = null;
        contextSize = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // llama.cpp shape: { "error": { "n_prompt_tokens": N, "n_ctx": M } }
            var scope = root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object
                ? errorEl
                : root;

            if (scope.TryGetProperty("n_prompt_tokens", out var promptEl)
                && promptEl.ValueKind == JsonValueKind.Number
                && promptEl.TryGetInt32(out var promptValue))
            {
                promptTokens = promptValue;
            }

            if (scope.TryGetProperty("n_ctx", out var ctxEl)
                && ctxEl.ValueKind == JsonValueKind.Number
                && ctxEl.TryGetInt32(out var ctxValue))
            {
                contextSize = ctxValue;
            }
        }
        catch (JsonException)
        {
            // Markers matched but body is not parseable JSON; token counts simply stay null.
        }
    }
}
