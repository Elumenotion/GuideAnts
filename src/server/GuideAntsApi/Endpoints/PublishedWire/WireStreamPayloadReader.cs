using System.Text;
using System.Text.Json;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireStreamPayloadReader
{
internal static void ReadUsagePayload(string payload, out long promptTokens, out long completionTokens)
{
    promptTokens = 0;
    completionTokens = 0;

    if (string.IsNullOrWhiteSpace(payload))
    {
        return;
    }

    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        promptTokens = ReadLongProperty(root, "promptTokens", "prompt_tokens");
        completionTokens = ReadLongProperty(root, "completionTokens", "completion_tokens");
    }
    catch (JsonException)
    {
        // best effort usage parsing
    }
}

internal static string? ReadContentPayload(string payload)
{
    if (string.IsNullOrWhiteSpace(payload))
    {
        return null;
    }

    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }
    }
    catch (JsonException)
    {
        // ignore malformed content payload
    }

    return null;
}

internal static string? ReadContentDeltaPayload(string payload)
{
    if (string.IsNullOrWhiteSpace(payload))
    {
        return null;
    }

    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.TryGetProperty("contentDelta", out var delta) && delta.ValueKind == JsonValueKind.String)
        {
            return delta.GetString();
        }
    }
    catch (JsonException)
    {
        // ignore malformed delta payload
    }

    return null;
}

internal static long ReadLongProperty(JsonElement root, string camelName, string snakeName)
{
    if (root.TryGetProperty(camelName, out var camelValue) && camelValue.ValueKind == JsonValueKind.Number && camelValue.TryGetInt64(out var camelLong))
    {
        return camelLong;
    }

    if (root.TryGetProperty(snakeName, out var snakeValue) && snakeValue.ValueKind == JsonValueKind.Number && snakeValue.TryGetInt64(out var snakeLong))
    {
        return snakeLong;
    }

    return 0;
}

internal static object BuildOpenAiUsage(long promptTokens, long completionTokens) =>
    new
    {
        prompt_tokens = promptTokens,
        completion_tokens = completionTokens,
        total_tokens = promptTokens + completionTokens
    };

internal static long EstimateAnthropicInputTokens(JsonElement request)
{
    try
    {
        var rawJson = request.GetRawText();
        var utf8Bytes = Encoding.UTF8.GetByteCount(rawJson);
        var estimated = (utf8Bytes + 3L) / 4L;
        return Math.Clamp(estimated, 1L, int.MaxValue);
    }
    catch
    {
        return 1;
    }
}
}
