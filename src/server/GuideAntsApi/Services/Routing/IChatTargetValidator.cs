using GuideAntsApi.Services.LlamaCpp;
using System.Text.Json;

namespace GuideAntsApi.Services.Routing;

public interface IChatTargetValidator
{
    /// <summary>
    /// Validates that the resolved chat target can actually dispatch:
    ///   - provider is supported
    ///   - required provider-section fields are populated in configuration
    ///   - llama-cpp targets have valid <c>LocalRuntimeJson</c> with a known runtime profile
    ///
    /// Per R-12.5/12.6 this method must NOT be called while a
    /// <c>NotebookModelRuntimeService</c> load is in flight: run it at chat dispatch
    /// time, not inside the load-operation lock.
    /// </summary>
    void Validate(ChatTarget target);
}

public sealed class ChatTargetValidator : IChatTargetValidator
{
    // Each provider string resolves to exactly one provider section. The
    // Azure-prefixed strings target the AzureOpenAI section; the unprefixed
    // strings target the OpenAI (platform) section. The set is closed; the
    // UI constrains input to these values (ModelsTab provider select) and the
    // RenameOpenAiChatProvidersToAzure migration rewrites any pre-split bare
    // "openai" / "openai-chat" / "openai-responses" rows to the canonical
    // azure-prefixed variants, so an unrecognized value here is a data bug
    // and must fail fast rather than being silently aliased.
    internal static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai-chat",
        "openai-responses",
        "azure-openai-chat",
        "azure-openai-responses",
        "anthropic",
        "llama-cpp",
        "google-gemini-chat",
        "hf-inference-chat",
        "openrouter-chat"
    };

    private readonly IConfiguration _configuration;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;

    public ChatTargetValidator(
        IConfiguration configuration,
        IRuntimeProfileResolver runtimeProfileResolver)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runtimeProfileResolver = runtimeProfileResolver ?? throw new ArgumentNullException(nameof(runtimeProfileResolver));
    }

    public void Validate(ChatTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var provider = target.Provider.Trim().ToLowerInvariant();
        if (!KnownProviders.Contains(provider))
        {
            throw new RoutingException(
                RoutingErrorCodes.ProviderNotReady,
                $"Provider '{target.Provider}' for model '{target.ModelId}' is not supported. "
                + "Expected one of: openai-chat, openai-responses, azure-openai-chat, azure-openai-responses, anthropic, llama-cpp, google-gemini-chat, hf-inference-chat, openrouter-chat.",
                action: $"Update model '{target.ModelId}' in Settings → Models & Runtime to one of the supported providers.",
                serviceId: "Chat",
                providerSection: target.Provider,
                modelId: target.ModelId);
        }

        switch (provider)
        {
            case "openai-chat":
            case "openai-responses":
                // OpenAI platform (api.openai.com) — only ApiKey is required.
                RequireFields(
                    providerSection: "OpenAI",
                    fields: new[] { "ApiKey" },
                    modelId: target.ModelId);
                break;

            case "azure-openai-chat":
            case "azure-openai-responses":
                // Azure OpenAI — requires Resource + ApiKey; Deployment is
                // optional (per-model override lives on the catalog row).
                RequireFields(
                    providerSection: "AzureOpenAI",
                    fields: new[] { "Resource", "ApiKey" },
                    modelId: target.ModelId);
                break;

            case "anthropic":
                RequireAnyOf(
                    providerSection: "Anthropic",
                    alternatives: new[] { "ApiKey", "AuthToken" },
                    modelId: target.ModelId);
                break;

            case "llama-cpp":
                ValidateLlamaCpp(target);
                break;

            case "google-gemini-chat":
                RequireFields(
                    providerSection: "GoogleGeminiApi",
                    fields: new[] { "ApiKey" },
                    modelId: target.ModelId);
                break;

            case "hf-inference-chat":
                RequireFields(
                    providerSection: "HuggingFace",
                    fields: new[] { "Token" },
                    modelId: target.ModelId);
                break;

            case "openrouter-chat":
                RequireFields(
                    providerSection: "OpenRouter",
                    fields: new[] { "ApiKey" },
                    modelId: target.ModelId);
                break;
        }
    }

    private void RequireFields(string providerSection, IEnumerable<string> fields, string modelId)
    {
        var blockers = new List<string>();
        foreach (var field in fields)
        {
            var value = _configuration[$"{providerSection}:{field}"];
            if (string.IsNullOrWhiteSpace(value))
            {
                blockers.Add($"{providerSection}:{field} is not configured.");
            }
        }

        if (blockers.Count > 0)
        {
            throw RoutingException.ProviderNotReady(
                providerSection,
                blockers,
                serviceId: "Chat",
                modeId: null);
        }
    }

    private void RequireAnyOf(string providerSection, IEnumerable<string> alternatives, string modelId)
    {
        var alternativeList = alternatives.ToList();
        var hasAny = alternativeList.Any(field => !string.IsNullOrWhiteSpace(_configuration[$"{providerSection}:{field}"]));
        if (!hasAny)
        {
            throw RoutingException.ProviderNotReady(
                providerSection,
                blockers: new[]
                {
                    $"{providerSection} requires one of: {string.Join(", ", alternativeList)}."
                },
                serviceId: "Chat");
        }
    }

    private void ValidateJsonFieldIfPresent(string providerSection, string field)
    {
        var value = _configuration[$"{providerSection}:{field}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            _ = JsonDocument.Parse(value);
        }
        catch (Exception ex)
        {
            throw RoutingException.ProviderNotReady(
                providerSection,
                blockers: new[] { $"{providerSection}:{field} is not valid JSON: {ex.Message}" },
                serviceId: "Chat");
        }
    }

    private void ValidateLlamaCpp(ChatTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.LocalRuntimeJson))
        {
            throw new RoutingException(
                RoutingErrorCodes.ModelNotReady,
                $"Model '{target.ModelId}' is configured as llama-cpp but is missing LocalRuntimeJson.",
                action: $"Open Settings → Models & Runtime → Catalog and set a LocalRuntime configuration for '{target.ModelId}'.",
                serviceId: "Chat",
                modelId: target.ModelId,
                providerSection: "LlamaCpp");
        }

        LocalRuntimeConfiguration parsed;
        try
        {
            parsed = LocalRuntimeConfigurationParser.ParseRequired(target.ModelId, target.LocalRuntimeJson);
        }
        catch (Exception ex)
        {
            throw new RoutingException(
                RoutingErrorCodes.ModelNotReady,
                $"Model '{target.ModelId}' has invalid LocalRuntimeJson: {ex.Message}",
                action: $"Fix the LocalRuntimeJson payload for '{target.ModelId}' in Settings → Models & Runtime → Catalog.",
                serviceId: "Chat",
                modelId: target.ModelId,
                providerSection: "LlamaCpp",
                innerException: ex);
        }

        try
        {
            _runtimeProfileResolver.ResolveAsync(parsed.RuntimeProfileId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new RoutingException(
                RoutingErrorCodes.RuntimeNotReady,
                $"Runtime profile '{parsed.RuntimeProfileId}' for model '{target.ModelId}' is unavailable: {ex.Message}",
                action: $"Ensure runtime profile '{parsed.RuntimeProfileId}' exists in Settings → Models & Runtime → Runtime Profiles.",
                serviceId: "Chat",
                modelId: target.ModelId,
                providerSection: "LlamaCpp",
                innerException: ex);
        }
    }
}
