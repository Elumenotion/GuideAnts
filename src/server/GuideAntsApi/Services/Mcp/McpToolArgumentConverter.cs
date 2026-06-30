using System.Text.Json;

namespace GuideAntsApi.Services.Mcp;

internal static class McpToolArgumentConverter
{
    public static Dictionary<string, object> Convert(IReadOnlyDictionary<string, object>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new Dictionary<string, object>();
        }

        var converted = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            converted[key] = value switch
            {
                JsonElement jsonElement => jsonElement.ValueKind switch
                {
                    JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText())
                        ?? new Dictionary<string, object>(),
                    JsonValueKind.Array => JsonSerializer.Deserialize<object[]>(jsonElement.GetRawText()) ?? [],
                    JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
                    JsonValueKind.Number => jsonElement.TryGetInt64(out var longValue) ? longValue : jsonElement.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null!,
                    _ => jsonElement.GetRawText(),
                },
                _ => value,
            };
        }

        return converted;
    }
}
