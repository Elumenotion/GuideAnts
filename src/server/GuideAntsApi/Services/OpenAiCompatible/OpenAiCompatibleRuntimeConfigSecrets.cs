using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.OpenAiCompatible;

/// <summary>
/// Row-scoped field encryption for the <c>apiKey</c> field of
/// <c>openai-compatible</c> RuntimeConfigJson. The row JSON travels in a plain
/// column, so the key is encrypted field-by-field with the settings-secret
/// keyring (same <c>enc::v2</c> envelope as section secrets). <c>baseUrl</c>
/// stays plaintext.
/// </summary>
public static class OpenAiCompatibleRuntimeConfigSecrets
{
    /// <summary>
    /// Client-side sentinel meaning "remove the stored API key" (an empty value
    /// means "keep the existing key", so "remove" needs its own signal).
    /// </summary>
    public const string RemoveKeySentinel = "__REMOVE__";

    /// <summary>
    /// Normalizes an incoming row config and returns the canonical JSON to persist:
    /// baseUrl validated (absolute http(s), no trailing slash) and kept plaintext;
    /// apiKey resolved per the update semantics —
    ///   • sentinel <see cref="RemoveKeySentinel"/>  → field removed,
    ///   • blank/absent                              → existing ciphertext preserved,
    ///   • non-empty plaintext                       → encrypted with the active key.
    /// Already-encrypted values pass through unchanged (no double-wrap).
    /// </summary>
    public static string NormalizeAndEncrypt(
        string modelId,
        string? incomingJson,
        string? existingJson,
        SettingsSecretsOptions options)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(incomingJson ?? string.Empty) as JsonObject;
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

        var baseUrlRaw = root.TryGetPropertyValue("baseUrl", out var baseUrlNode)
            ? ApplicationSettingsJson.NodeToString(baseUrlNode)
            : string.Empty;
        var baseUrl = OpenAiCompatibleRuntimeConfigurationParser
            .ValidateBaseUrlForWrite(modelId, baseUrlRaw);

        var apiKeyRaw = root.TryGetPropertyValue("apiKey", out var apiKeyNode)
            ? ApplicationSettingsJson.NodeToString(apiKeyNode)
            : string.Empty;

        string? apiKeyOut = null;
        if (string.Equals(apiKeyRaw, RemoveKeySentinel, StringComparison.Ordinal))
        {
            apiKeyOut = null;
        }
        else if (string.IsNullOrWhiteSpace(apiKeyRaw))
        {
            // Keep the existing ciphertext byte-for-byte (byte-stable saves).
            apiKeyOut = ReadExistingEncryptedKey(modelId, existingJson);
        }
        else
        {
            apiKeyOut = ApplicationSettingsJson.EncryptSecretValue(apiKeyRaw, options);
        }

        var canonical = new JsonObject
        {
            ["baseUrl"] = baseUrl
        };
        if (!string.IsNullOrWhiteSpace(apiKeyOut))
        {
            canonical["apiKey"] = apiKeyOut;
        }

        return ApplicationSettingsJson.Serialize(canonical);
    }

    /// <summary>
    /// True when the stored row JSON carries an <c>enc::</c>/<c>encv2::</c>-wrapped
    /// apiKey — the client renders that as "key set" (<c>•••</c>) and never the value.
    /// </summary>
    public static bool HasEncryptedKey(string? runtimeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(runtimeConfigJson))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(runtimeConfigJson) as JsonObject;
            if (root is null || !root.TryGetPropertyValue("apiKey", out var node))
            {
                return false;
            }

            return ApplicationSettingsJson.IsEncryptedSecretValue(ApplicationSettingsJson.NodeToString(node));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadExistingEncryptedKey(string modelId, string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(existingJson) as JsonObject;
            if (root is null || !root.TryGetPropertyValue("apiKey", out var node))
            {
                return null;
            }

            var raw = ApplicationSettingsJson.NodeToString(node);
            return ApplicationSettingsJson.IsEncryptedSecretValue(raw) ? raw : null;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' existing RuntimeConfigJson is invalid JSON; cannot preserve the stored apiKey.", ex);
        }
    }
}
