using System.Text.Json;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Maps persisted <c>ChatDefaults</c> configuration keys to the execution parameter bag.
/// Empty or whitespace configuration values are treated as absent.
/// </summary>
internal static class ChatDefaultsExecutionParameters
{
    private const string SectionPrefix = "ChatDefaults:";

    public static IReadOnlyDictionary<string, JsonElement> FromConfiguration(IConfiguration configuration, string modelId)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var temperature = ReadOptionalFloat(configuration, $"{SectionPrefix}Temperature");
        if (temperature.HasValue)
        {
            result["temperature"] = JsonSerializer.SerializeToElement((double)temperature.Value);
        }

        var topP = ReadOptionalFloat(configuration, $"{SectionPrefix}TopP");
        if (topP.HasValue)
        {
            result["top_p"] = JsonSerializer.SerializeToElement(topP.Value);
        }

        var reasoningEffort = ReadOptionalString(configuration, $"{SectionPrefix}ReasoningEffort");
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            result["reasoning_effort"] = JsonSerializer.SerializeToElement(reasoningEffort);
        }

        var samplingJson = configuration[$"{SectionPrefix}SamplingParametersJson"];
        if (string.IsNullOrWhiteSpace(samplingJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(samplingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw RoutingException.ModelNotReady(
                    modelId,
                    "ChatDefaults:SamplingParametersJson must be a JSON object.",
                    serviceId: "Chat",
                    action: "Open Settings \u2192 Overview \u2192 Default Chat Model and provide a valid sampling JSON object.");
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException ex)
        {
            throw RoutingException.ModelNotReady(
                modelId,
                $"ChatDefaults:SamplingParametersJson is invalid JSON: {ex.Message}",
                serviceId: "Chat",
                action: "Open Settings \u2192 Overview \u2192 Default Chat Model and fix SamplingParametersJson.");
        }

        return result;
    }

    private static float? ReadOptionalFloat(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private static string? ReadOptionalString(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
