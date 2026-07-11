using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed record LocalRuntimeConfiguration(
    string RouterModelId,
    string RuntimeProfileId);

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

        return new LocalRuntimeConfiguration(
            routerModelId,
            parsed.RuntimeProfileId!.Trim());
    }

    public static string SerializeCanonical(LocalRuntimeConfiguration configuration)
    {
        var root = new JsonObject
        {
            ["routerModelId"] = configuration.RouterModelId,
            ["runtimeProfileId"] = configuration.RuntimeProfileId
        };

        return root.ToJsonString(CanonicalJsonOptions);
    }

    private sealed record LocalRuntimeConfigurationPayload(
        string? RouterModelId,
        string? RuntimeProfileId);
}

/// <summary>
/// Reads legacy runtime JSON fields for one-time migration. Not used by the final parser.
/// </summary>
public static class LocalRuntimeConfigurationMigrationReader
{
    private static readonly JsonSerializerOptions DeserializeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LegacyLocalRuntimeConfiguration ReadLegacy(string modelId, string runtimeConfigJson)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(runtimeConfigJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson is invalid JSON.", ex);
        }

        if (root is null)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson must be a JSON object.");
        }

        var payload = root.Deserialize<LegacyLocalRuntimeConfigurationPayload>(DeserializeJsonOptions)
            ?? new LegacyLocalRuntimeConfigurationPayload();

        JsonObject? loadParams = null;
        if (root.TryGetPropertyValue("loadParams", out var loadParamsNode) && loadParamsNode is JsonObject loadObj)
        {
            loadParams = loadObj.DeepClone().AsObject();
        }

        return new LegacyLocalRuntimeConfiguration(
            payload.RouterModelId?.Trim() ?? string.Empty,
            payload.RuntimeProfileId?.Trim() ?? string.Empty,
            loadParams,
            payload.ParallelToolCalls,
            payload.RouterContextSize,
            payload.RouterCacheRamMib);
    }

    private sealed class LegacyLocalRuntimeConfigurationPayload
    {
        public string? RouterModelId { get; set; }
        public string? RuntimeProfileId { get; set; }
        public bool? ParallelToolCalls { get; set; }
        public int? RouterContextSize { get; set; }
        public int? RouterCacheRamMib { get; set; }
    }
}

public sealed record LegacyLocalRuntimeConfiguration(
    string RouterModelId,
    string RuntimeProfileId,
    JsonObject? LoadParams,
    bool? ParallelToolCalls,
    int? RouterContextSize = null,
    int? RouterCacheRamMib = null);
