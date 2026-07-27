using System.Text.Json;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Maps persisted <c>ChatDefaults</c> to the execution parameter bag.
/// Empty or whitespace values are treated as absent.
/// </summary>
internal static class ChatDefaultsExecutionParameters
{
    public static IReadOnlyDictionary<string, JsonElement> FromSnapshot(ChatDefaultsSnapshot snapshot, string modelId)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (snapshot.Temperature is { } temperature)
        {
            result["temperature"] = JsonSerializer.SerializeToElement((double)temperature);
        }

        if (snapshot.TopP is { } topP)
        {
            result["top_p"] = JsonSerializer.SerializeToElement(topP);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ReasoningEffort))
        {
            result["reasoning_effort"] = JsonSerializer.SerializeToElement(snapshot.ReasoningEffort);
        }

        var samplingJson = snapshot.SamplingParametersJson;
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
}
