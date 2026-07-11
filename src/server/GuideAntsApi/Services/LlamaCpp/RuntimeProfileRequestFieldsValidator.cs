using System.Text.Json;

namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Validates runtime profile tool-request field JSON.
/// </summary>
public static class RuntimeProfileRequestFieldsValidator
{
    private static readonly HashSet<string> ReservedTransportFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "messages",
        "stream",
        "stream_options",
        "tools",
        "tool_choice"
    };

    public static IReadOnlyDictionary<string, JsonElement> ValidateAndNormalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("RequestFieldsWhenToolsPresentJson is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "RequestFieldsWhenToolsPresentJson must be a JSON object.");
            }

            var normalized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    throw new InvalidOperationException(
                        "RequestFieldsWhenToolsPresentJson contains a blank key.");
                }

                if (ReservedTransportFields.Contains(property.Name))
                {
                    throw new InvalidOperationException(
                        $"RequestFieldsWhenToolsPresentJson key '{property.Name}' is reserved for transport.");
                }

                if (!IsSupportedValueKind(property.Value.ValueKind))
                {
                    throw new InvalidOperationException(
                        $"RequestFieldsWhenToolsPresentJson key '{property.Name}' must be a JSON primitive.");
                }

                normalized[property.Name] = property.Value.Clone();
            }

            return normalized;
        }
    }

    public static string NormalizeJsonString(string? json)
    {
        var normalized = ValidateAndNormalize(json);
        if (normalized.Count == 0)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(
            normalized.ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.Deserialize<object>(pair.Value.GetRawText())!));
    }

    private static bool IsSupportedValueKind(JsonValueKind kind) =>
        kind is JsonValueKind.True
            or JsonValueKind.False
            or JsonValueKind.Number
            or JsonValueKind.String
            or JsonValueKind.Null;
}
