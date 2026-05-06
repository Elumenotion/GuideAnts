using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed record LocalRuntimeConfiguration(
    string RouterModelId,
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

    private static readonly JsonSerializerOptions DeserializeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LocalRuntimeConfiguration ParseRequired(string modelId, string? runtimeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(runtimeConfigJson))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' is configured as llama-cpp but is missing RuntimeConfigJson.");
        }

        return Parse(modelId, runtimeConfigJson);
    }

    public static LocalRuntimeConfiguration Parse(string modelId, string runtimeConfigJson)
    {
        LocalRuntimeConfigurationPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LocalRuntimeConfigurationPayload>(
                runtimeConfigJson,
                DeserializeJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson is invalid JSON.", ex);
        }

        if (parsed is null)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson must be a JSON object.");
        }

        var missingFields = new List<string>();
        if (string.IsNullOrWhiteSpace(parsed.RouterModelId))
        {
            missingFields.Add("routerModelId");
        }

        if (string.IsNullOrWhiteSpace(parsed.RuntimeProfileId))
        {
            missingFields.Add("runtimeProfileId");
        }

        if (missingFields.Count > 0)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson is missing required field(s): {string.Join(", ", missingFields)}.");
        }

        var routerModelId = parsed.RouterModelId!.Trim();
        if (routerModelId.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson field 'routerModelId' must not include '.gguf' suffix.");
        }

        ValidateOptionalRange(modelId, "routerContextSize", parsed.RouterContextSize, min: 1024, max: 1_048_576);
        ValidateOptionalRange(modelId, "routerCacheRamMib", parsed.RouterCacheRamMib, min: 0, max: 262_144);

        return new LocalRuntimeConfiguration(
            routerModelId,
            parsed.RuntimeProfileId!.Trim(),
            parsed.LoadParams,
            parsed.ParallelToolCalls ?? false,
            parsed.RouterContextSize,
            parsed.RouterCacheRamMib);
    }

    public static string SerializeCanonical(LocalRuntimeConfiguration configuration)
    {
        var root = new JsonObject
        {
            ["routerModelId"] = configuration.RouterModelId,
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

    private static void ValidateOptionalRange(string modelId, string fieldName, int? value, int min, int max)
    {
        if (value is null)
        {
            return;
        }

        if (value.Value < min || value.Value > max)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson property '{fieldName}' must be between {min} and {max}.");
        }
    }

    private sealed record LocalRuntimeConfigurationPayload(
        string? RouterModelId,
        string? RuntimeProfileId,
        JsonObject? LoadParams,
        bool? ParallelToolCalls,
        int? RouterContextSize = null,
        int? RouterCacheRamMib = null);
}
