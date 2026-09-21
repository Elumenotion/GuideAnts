using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed record LocalRuntimeConfiguration(
    string RouterModelId,
    string StackBaseUrl = "",
    string StackApiKey = "")
{
    /// <summary>
    /// True when this row targets the global LlamaCpp:BaseUrl (no row-owned stack).
    /// </summary>
    public bool UsesGlobalStack => string.IsNullOrWhiteSpace(StackBaseUrl);
}

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

        if (string.IsNullOrWhiteSpace(parsed.RouterModelId))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson is missing required field(s): routerModelId.");
        }

        var routerModelId = parsed.RouterModelId.Trim();
        if (routerModelId.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson field 'routerModelId' must not include '.gguf' suffix.");
        }

        var stackBaseUrl = ValidateStackBaseUrl(modelId, parsed.StackBaseUrl);
        var stackApiKey = (parsed.StackApiKey ?? string.Empty).Trim();

        return new LocalRuntimeConfiguration(routerModelId, stackBaseUrl, stackApiKey);
    }

    /// <summary>
    /// The row-owned stack base URL for this row, or null when the row targets the
    /// global LlamaCpp:BaseUrl. Malformed JSON returns null (row unusable) rather than
    /// throwing: callers that aggregate rows (stack universe discovery) must degrade
    /// gracefully per row.
    /// </summary>
    public static string? ParseStackBaseUrl(string? runtimeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(runtimeConfigJson))
        {
            return null;
        }

        try
        {
            return Parse(string.Empty, runtimeConfigJson).StackBaseUrl;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The row-owned stack is an AI-stack root URL (no service prefix) pointing at
    /// the same nginx surface as the global LlamaCpp:BaseUrl minus the /llama-cpp
    /// suffix. Absolute http(s) only, no trailing slash. Empty/whitespace returns
    /// string.Empty (row targets the global stack).
    /// </summary>
    private static string ValidateStackBaseUrl(string modelId, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        if (value.EndsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson field 'stackBaseUrl' must not include a trailing '/' (configure the stack root, e.g. http://192.0.2.1:8112).");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson field 'stackBaseUrl' must be an absolute http(s) URL (got '{value}').");
        }

        return value;
    }

    public static string SerializeCanonical(LocalRuntimeConfiguration configuration)
    {
        var root = new JsonObject
        {
            ["routerModelId"] = configuration.RouterModelId
        };
        if (!string.IsNullOrEmpty(configuration.StackBaseUrl))
        {
            root["stackBaseUrl"] = configuration.StackBaseUrl;
        }
        if (!string.IsNullOrEmpty(configuration.StackApiKey))
        {
            root["stackApiKey"] = configuration.StackApiKey;
        }

        return root.ToJsonString(CanonicalJsonOptions);
    }

    private sealed record LocalRuntimeConfigurationPayload(string? RouterModelId, string? StackBaseUrl, string? StackApiKey);
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
