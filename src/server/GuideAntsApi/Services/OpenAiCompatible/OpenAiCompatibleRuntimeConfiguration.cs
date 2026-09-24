using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.OpenAiCompatible;

/// <summary>
/// Row-owned connection details for the <c>openai-compatible</c> provider
/// (stored in <c>Models.RuntimeConfigJson</c>). The endpoint is the v1 base:
/// the client POSTs to <c>{BaseUrl}/chat/completions</c>.
/// </summary>
public sealed record OpenAiCompatibleRuntimeConfiguration(
    string BaseUrl,
    string ApiKey = "")
{
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}

/// <summary>
/// Parses the row's <c>RuntimeConfigJson</c> for provider <c>openai-compatible</c>.
/// Mirrors <see cref="GuideAntsApi.Services.LlamaCpp.LocalRuntimeConfigurationParser"/>:
/// camelCase JSON, model-scoped validation messages. The <c>apiKey</c> field is
/// encrypted at rest with the settings keyring; <see cref="Parse"/> decrypts it
/// (plaintext pass-through for legacy values) so routing gets a usable key.
/// </summary>
public static class OpenAiCompatibleRuntimeConfigurationParser
{
    /// <summary>
    /// Parses the row config. Returns <c>null</c> when the row has no config at all
    /// (the caller decides how a missing row config is an error — no silent fallback).
    /// Throws <see cref="InvalidOperationException"/> when the config is present but
    /// invalid (missing baseUrl, non-http(s) scheme, trailing slash, undecryptable key).
    /// </summary>
    public static OpenAiCompatibleRuntimeConfiguration? Parse(
        string modelId,
        string? runtimeConfigJson,
        SettingsSecretsOptions? secretsOptions = null,
        IDataProtector? legacyProtector = null)
    {
        if (string.IsNullOrWhiteSpace(runtimeConfigJson))
        {
            return null;
        }

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

        var baseUrlRaw = root.TryGetPropertyValue("baseUrl", out var baseUrlNode)
            ? ApplicationSettingsJson.NodeToString(baseUrlNode)
            : string.Empty;
        var baseUrl = ValidateBaseUrl(modelId, baseUrlRaw, required: true);

        var apiKeyRaw = root.TryGetPropertyValue("apiKey", out var apiKeyNode)
            ? ApplicationSettingsJson.NodeToString(apiKeyNode)
            : string.Empty;
        var apiKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(apiKeyRaw))
        {
            if (ApplicationSettingsJson.IsEncryptedSecretValue(apiKeyRaw))
            {
                if (secretsOptions is null)
                {
                    throw new InvalidOperationException(
                        $"Model '{modelId}' has an encrypted apiKey but no settings keyring is available to decrypt it.");
                }

                apiKey = ApplicationSettingsJson.DecryptSecretValue(apiKeyRaw, secretsOptions, legacyProtector)
                    ?? string.Empty;
            }
            else
            {
                apiKey = apiKeyRaw.Trim();
            }
        }

        return new OpenAiCompatibleRuntimeConfiguration(baseUrl, apiKey);
    }

    /// <summary>
    /// Like <see cref="Parse"/> but throws when the row has no config at all.
    /// </summary>
    public static OpenAiCompatibleRuntimeConfiguration ParseRequired(
        string modelId,
        string? runtimeConfigJson,
        SettingsSecretsOptions? secretsOptions = null,
        IDataProtector? legacyProtector = null)
    {
        return Parse(modelId, runtimeConfigJson, secretsOptions, legacyProtector)
            ?? throw new InvalidOperationException(
                $"Model '{modelId}' is configured as openai-compatible but is missing RuntimeConfigJson.");
    }

    /// <summary>
    /// The row's baseUrl when present and valid — no decryption, no apiKey handling.
    /// Used by the dispatch validator and the readiness service, which care about the
    /// endpoint being usable, not the credentials. False for missing/invalid config.
    /// </summary>
    public static bool TryParseBaseUrl(string? runtimeConfigJson, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(runtimeConfigJson))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(runtimeConfigJson) as JsonObject;
            if (root is null)
            {
                return false;
            }

            var raw = root.TryGetPropertyValue("baseUrl", out var node)
                ? ApplicationSettingsJson.NodeToString(node)
                : string.Empty;
            baseUrl = ValidateBaseUrl(string.Empty, raw, required: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Public baseUrl validation for the write path (S7). Blank is allowed and
    /// returns <c>string.Empty</c>; present values must be absolute http(s) with no
    /// trailing slash.
    /// </summary>
    public static string ValidateBaseUrlForWrite(string modelId, string? raw)
        => ValidateBaseUrl(modelId, raw, required: false);

    /// <summary>
    /// baseUrl is the v1 base of the OpenAI-compatible endpoint: absolute http(s),
    /// no trailing slash (the client appends <c>/chat/completions</c>).
    /// </summary>
    private static string ValidateBaseUrl(string modelId, string? raw, bool required)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!required)
            {
                return string.Empty;
            }

            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson is missing required field(s): baseUrl.");
        }

        if (value.EndsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson field 'baseUrl' must not include a trailing '/' (configure the v1 base, e.g. http://localhost:8000/v1).");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' RuntimeConfigJson field 'baseUrl' must be an absolute http(s) URL (got '{value}').");
        }

        return value;
    }
}
