using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed record LocalRuntimeConfiguration(
    string RouterModelId,
    string ResourceGroupKey,
    string RuntimeProfileId,
    JsonObject? LoadParams,
    bool ParallelToolCalls,
    int? RouterContextSize = null,
    int? RouterCacheRamMib = null);

public static class LocalRuntimeConfigurationParser
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static LocalRuntimeConfiguration ParseRequired(string modelId, string? localRuntimeJson)
    {
        if (string.IsNullOrWhiteSpace(localRuntimeJson))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' is configured as llama-cpp but is missing LocalRuntimeJson.");
        }

        return Parse(modelId, localRuntimeJson);
    }

    public static LocalRuntimeConfiguration Parse(string modelId, string localRuntimeJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(localRuntimeJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson is invalid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' LocalRuntimeJson must be a JSON object.");
            }

            var unknownFields = new List<string>();
            string? routerModelId = null;
            string? resourceGroupKey = null;
            string? runtimeProfileId = null;
            JsonObject? loadParams = null;
            bool? parallelToolCalls = null;
            int? routerContextSize = null;
            int? routerCacheRamMib = null;

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "routerModelId":
                        routerModelId = ReadRequiredString(property, modelId, "routerModelId");
                        break;
                    case "resourceGroupKey":
                        resourceGroupKey = ReadRequiredString(property, modelId, "resourceGroupKey");
                        break;
                    case "runtimeProfileId":
                        runtimeProfileId = ReadRequiredString(property, modelId, "runtimeProfileId");
                        break;
                    case "routerContextSize":
                        routerContextSize = ReadOptionalPositiveInt(property, modelId, "routerContextSize", min: 1024, max: 1_048_576);
                        break;
                    case "routerCacheRamMib":
                        routerCacheRamMib = ReadOptionalNonNegativeInt(property, modelId, "routerCacheRamMib", max: 262_144);
                        break;
                    case "loadParams":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            loadParams = null;
                            break;
                        }

                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidOperationException(
                                $"Model '{modelId}' LocalRuntimeJson property 'loadParams' must be a JSON object.");
                        }

                        loadParams = JsonNode.Parse(property.Value.GetRawText()) as JsonObject
                                     ?? throw new InvalidOperationException(
                                         $"Model '{modelId}' LocalRuntimeJson property 'loadParams' must be a JSON object.");
                        break;
                    case "parallelToolCalls":
                        parallelToolCalls = ReadOptionalBoolean(property, modelId, "parallelToolCalls");
                        break;
                    default:
                        unknownFields.Add(property.Name);
                        break;
                }
            }

            if (unknownFields.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' LocalRuntimeJson contains unsupported field(s): {string.Join(", ", unknownFields)}.");
            }

            var missingFields = new List<string>();
            if (string.IsNullOrWhiteSpace(routerModelId))
            {
                missingFields.Add("routerModelId");
            }

            if (string.IsNullOrWhiteSpace(resourceGroupKey))
            {
                missingFields.Add("resourceGroupKey");
            }

            if (string.IsNullOrWhiteSpace(runtimeProfileId))
            {
                missingFields.Add("runtimeProfileId");
            }

            if (missingFields.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' LocalRuntimeJson is missing required field(s): {string.Join(", ", missingFields)}.");
            }

            if (routerModelId!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' LocalRuntimeJson field 'routerModelId' must not include '.gguf' suffix.");
            }

            return new LocalRuntimeConfiguration(
                routerModelId.Trim(),
                resourceGroupKey!.Trim(),
                runtimeProfileId!.Trim(),
                loadParams,
                parallelToolCalls ?? false,
                routerContextSize,
                routerCacheRamMib);
        }
    }

    public static string SerializeCanonical(LocalRuntimeConfiguration configuration)
    {
        var root = new JsonObject
        {
            ["routerModelId"] = configuration.RouterModelId,
            ["resourceGroupKey"] = configuration.ResourceGroupKey,
            ["runtimeProfileId"] = configuration.RuntimeProfileId
        };

        if (configuration.LoadParams is not null)
        {
            root["loadParams"] = configuration.LoadParams.DeepClone();
        }

        if (configuration.ParallelToolCalls)
        {
            root["parallelToolCalls"] = true;
        }

        if (configuration.RouterContextSize is not null)
        {
            root["routerContextSize"] = configuration.RouterContextSize.Value;
        }

        if (configuration.RouterCacheRamMib is not null)
        {
            root["routerCacheRamMib"] = configuration.RouterCacheRamMib.Value;
        }

        return root.ToJsonString(CanonicalJsonOptions);
    }

    private static int? ReadOptionalPositiveInt(JsonProperty property, string modelId, string fieldName, int min, int max)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var v))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must be a positive integer.");
        }

        if (v < min || v > max)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must be between {min} and {max}.");
        }

        return v;
    }

    private static int? ReadOptionalNonNegativeInt(JsonProperty property, string modelId, string fieldName, int max)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var v))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must be an integer.");
        }

        if (v < 0 || v > max)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must be between 0 and {max}.");
        }

        return v;
    }

    private static string ReadRequiredString(JsonProperty property, string modelId, string fieldName)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must be a string.");
        }

        var value = property.Value.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must not be empty.");
        }

        return value;
    }

    private static bool ReadOptionalBoolean(JsonProperty property, string modelId, string fieldName)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' LocalRuntimeJson property '{fieldName}' must be a boolean.");
        }

        return property.Value.GetBoolean();
    }
}
