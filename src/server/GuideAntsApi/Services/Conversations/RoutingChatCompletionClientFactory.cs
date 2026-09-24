using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using AntRunner.Chat.GoogleGemini;
using AntRunner.Chat.HuggingFace;
using AntRunner.Chat.LlamaCpp;
using AntRunner.Chat.OpenAI;
using AntRunner.Chat.OpenAiCompatible;
using AntRunner.Chat.OpenRouter;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.OpenAiCompatible;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Conversations;

public sealed class RoutingChatCompletionClientFactory : IChatCompletionClientFactory
{
    public const string OpenAiPlatformFactoryKey = "openai-platform";
    public const string AzureOpenAiFactoryKey = "azure-openai";

    private readonly OpenAiChatClientFactory _openAiPlatformChatFactory;
    private readonly OpenAiChatClientFactory _azureOpenAiChatFactory;
    private readonly OpenAiResponsesClientFactory _openAiPlatformResponsesFactory;
    private readonly OpenAiResponsesClientFactory _azureOpenAiResponsesFactory;
    private readonly AnthropicChatClientFactory _anthropicFactory;
    private readonly GoogleGeminiChatClientFactory _googleGeminiFactory;
    private readonly HuggingFaceChatClientFactory _huggingFaceFactory;
    private readonly OpenRouterChatClientFactory _openRouterFactory;
    private readonly OpenAiCompatibleChatClientFactory _openAiCompatibleFactory;
    private readonly LlamaCppChatClientFactory _llamaCppFactory;
    private readonly IChatTargetResolver _chatTargetResolver;
    private readonly IChatTargetValidator _chatTargetValidator;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<SettingsSecretsOptions> _settingsSecretsOptions;
    private readonly ILogger<RoutingChatCompletionClientFactory> _logger;

    public RoutingChatCompletionClientFactory(
        [FromKeyedServices(OpenAiPlatformFactoryKey)] OpenAiChatClientFactory openAiPlatformChatFactory,
        [FromKeyedServices(AzureOpenAiFactoryKey)] OpenAiChatClientFactory azureOpenAiChatFactory,
        [FromKeyedServices(OpenAiPlatformFactoryKey)] OpenAiResponsesClientFactory openAiPlatformResponsesFactory,
        [FromKeyedServices(AzureOpenAiFactoryKey)] OpenAiResponsesClientFactory azureOpenAiResponsesFactory,
        AnthropicChatClientFactory anthropicFactory,
        GoogleGeminiChatClientFactory googleGeminiFactory,
        HuggingFaceChatClientFactory huggingFaceFactory,
        OpenRouterChatClientFactory openRouterFactory,
        OpenAiCompatibleChatClientFactory openAiCompatibleFactory,
        LlamaCppChatClientFactory llamaCppFactory,
        IChatTargetResolver chatTargetResolver,
        IChatTargetValidator chatTargetValidator,
        IConfiguration configuration,
        IOptionsMonitor<SettingsSecretsOptions> settingsSecretsOptions,
        ILogger<RoutingChatCompletionClientFactory>? logger = null)
    {
        _openAiPlatformChatFactory = openAiPlatformChatFactory ?? throw new ArgumentNullException(nameof(openAiPlatformChatFactory));
        _azureOpenAiChatFactory = azureOpenAiChatFactory ?? throw new ArgumentNullException(nameof(azureOpenAiChatFactory));
        _openAiPlatformResponsesFactory = openAiPlatformResponsesFactory ?? throw new ArgumentNullException(nameof(openAiPlatformResponsesFactory));
        _azureOpenAiResponsesFactory = azureOpenAiResponsesFactory ?? throw new ArgumentNullException(nameof(azureOpenAiResponsesFactory));
        _anthropicFactory = anthropicFactory ?? throw new ArgumentNullException(nameof(anthropicFactory));
        _googleGeminiFactory = googleGeminiFactory ?? throw new ArgumentNullException(nameof(googleGeminiFactory));
        _huggingFaceFactory = huggingFaceFactory ?? throw new ArgumentNullException(nameof(huggingFaceFactory));
        _openRouterFactory = openRouterFactory ?? throw new ArgumentNullException(nameof(openRouterFactory));
        _openAiCompatibleFactory = openAiCompatibleFactory ?? throw new ArgumentNullException(nameof(openAiCompatibleFactory));
        _llamaCppFactory = llamaCppFactory ?? throw new ArgumentNullException(nameof(llamaCppFactory));
        _chatTargetResolver = chatTargetResolver ?? throw new ArgumentNullException(nameof(chatTargetResolver));
        _chatTargetValidator = chatTargetValidator ?? throw new ArgumentNullException(nameof(chatTargetValidator));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _settingsSecretsOptions = settingsSecretsOptions ?? throw new ArgumentNullException(nameof(settingsSecretsOptions));
        _logger = logger ?? NullLogger<RoutingChatCompletionClientFactory>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var target = _chatTargetResolver.Resolve(deploymentId);
        _chatTargetValidator.Validate(target);

        var provider = ParseProvider(target);
        _logger.LogInformation(
            "Chat provider route resolved. RequestedModelId={RequestedModelId}, CatalogModelId={CatalogModelId}, Provider={Provider}",
            LogValueSanitizer.Sanitize(string.IsNullOrWhiteSpace(deploymentId) ? "(unset)" : deploymentId),
            LogValueSanitizer.Sanitize(target.ModelId),
            LogValueSanitizer.Sanitize(target.Provider));

        if (provider == Provider.LlamaCpp)
        {
            var localRuntime = LocalRuntimeConfigurationParser.ParseRequired(target.ModelId, target.RuntimeConfigJson);
            if (target.ChatBehavior?.ThinkingControl?.ChoiceActions is not { Count: > 0 })
            {
                throw new RoutingException(
                    RoutingErrorCodes.ModelNotReady,
                    $"Model '{target.ModelId}' is missing model-owned chat behavior configuration.",
                    action: $"Open Settings → Models & Runtime and configure chat behavior for '{target.ModelId}'.",
                    serviceId: "Chat",
                    modelId: target.ModelId,
                    providerSection: "LlamaCpp");
            }

            var llamaProfile = ToLlamaCppProfileData(target.ChatBehavior);

            return _llamaCppFactory.CreateClientForProfile(
                localRuntime.RouterModelId,
                llamaProfile,
                BuildLlamaCppConfigOverride(localRuntime),
                httpClient);
        }

        return provider switch
        {
            Provider.Anthropic => _anthropicFactory.CreateClient(target.ModelId, httpClient),
            Provider.OpenAiPlatformChat => _openAiPlatformChatFactory.CreateClient(target.ModelId, httpClient),
            Provider.OpenAiPlatformResponses => _openAiPlatformResponsesFactory.CreateClient(target.ModelId, httpClient),
            Provider.AzureOpenAiChat => _azureOpenAiChatFactory.CreateClient(target.ModelId, httpClient),
            Provider.AzureOpenAiResponses => _azureOpenAiResponsesFactory.CreateClient(target.ModelId, httpClient),
            Provider.GoogleGeminiChat => _googleGeminiFactory.CreateClient(target.ModelId, httpClient),
            Provider.HuggingFaceInferenceChat => _huggingFaceFactory.CreateClientForBehavior(
                target.ModelId,
                ToProviderChatBehavior(target.ChatBehavior),
                httpClient),
            Provider.OpenRouterChat => _openRouterFactory.CreateClientForBehavior(
                target.ModelId,
                ToProviderChatBehavior(target.ChatBehavior),
                httpClient),
            Provider.OpenAiCompatible => CreateOpenAiCompatibleClient(target, httpClient),
            _ => throw new RoutingException(
                RoutingErrorCodes.ProviderNotReady,
                $"Unsupported provider for model '{target.ModelId}'.",
                action: $"Change the provider for '{target.ModelId}' in Settings → Models & Runtime → Catalog.",
                serviceId: "Chat",
                modelId: target.ModelId,
                providerSection: target.Provider)
        };
    }

    private static Provider ParseProvider(ChatTarget target) => target.Provider.Trim().ToLowerInvariant() switch
    {
        "openai-chat" => Provider.OpenAiPlatformChat,
        "openai-responses" => Provider.OpenAiPlatformResponses,
        "azure-openai-chat" => Provider.AzureOpenAiChat,
        "azure-openai-responses" => Provider.AzureOpenAiResponses,
        "anthropic" => Provider.Anthropic,
        "llama-cpp" => Provider.LlamaCpp,
        "google-gemini-chat" => Provider.GoogleGeminiChat,
        "hf-inference-chat" => Provider.HuggingFaceInferenceChat,
        "openrouter-chat" => Provider.OpenRouterChat,
        "openai-compatible" => Provider.OpenAiCompatible,
        _ => throw new RoutingException(
            RoutingErrorCodes.ProviderNotReady,
            $"Provider '{target.Provider}' is not supported. Expected openai-chat, openai-responses, azure-openai-chat, azure-openai-responses, anthropic, llama-cpp, google-gemini-chat, hf-inference-chat, openrouter-chat, or openai-compatible.",
            action: $"Change the provider for '{target.ModelId}' in Settings → Models & Runtime → Catalog.",
            serviceId: "Chat",
            modelId: target.ModelId,
            providerSection: target.Provider)
    };

    private enum Provider
    {
        OpenAiPlatformChat,
        OpenAiPlatformResponses,
        AzureOpenAiChat,
        AzureOpenAiResponses,
        Anthropic,
        LlamaCpp,
        GoogleGeminiChat,
        HuggingFaceInferenceChat,
        OpenRouterChat,
        OpenAiCompatible
    }

    /// <summary>
    /// Projects the row-owned chat behavior onto the provider-agnostic shape used by the
    /// OpenAI-compatible clients. Returns null when the row configures neither thinking control
    /// nor extra request fields so those clients keep their built-in request mapping.
    /// </summary>
    internal static ProviderChatBehavior? ToProviderChatBehavior(RuntimeProfileData? data)
    {
        if (data == null)
        {
            return null;
        }

        var thinking = data.ThinkingControl?.ChoiceActions is { Count: > 0 } ? data.ThinkingControl : null;
        var hasExtraFields = data.RequestFieldsWhenToolsPresent is { Count: > 0 };
        // The combine flag defaults to true on every catalog row, so a row that configures
        // nothing else still projects a behavior object: the OpenAI-compatible clients then
        // merge multiple system/developer messages (strict Qwen templates reject the rest).
        if (thinking == null && !hasExtraFields && !data.CombineSystemAndDeveloperMessages)
        {
            return null;
        }

        var thinkingControl = thinking == null
            ? null
            : new ProviderThinkingControl(
                thinking.DefaultChoice,
                thinking.ChoiceActions.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<ProviderChatBehaviorAction>)kvp.Value.Select(action =>
                        new ProviderChatBehaviorAction(
                            (ProviderChatBehaviorActionTarget)(int)action.Target,
                            action.Key,
                            action.Value)).ToList()));

        return new ProviderChatBehavior(
            thinkingControl,
            hasExtraFields ? data.RequestFieldsWhenToolsPresent : null,
            data.CombineSystemAndDeveloperMessages);
    }

    /// <summary>
    /// Builds an openai-compatible client from the row's RuntimeConfigJson. No silent
    /// fallback (R-9.1): a missing/invalid row config throws a RoutingException. The
    /// apiKey is decrypted from its enc::v2 envelope at routing time.
    /// </summary>
    private IChatCompletionClient CreateOpenAiCompatibleClient(ChatTarget target, HttpClient? httpClient)
    {
        OpenAiCompatibleRuntimeConfiguration config;
        try
        {
            config = OpenAiCompatibleRuntimeConfigurationParser.ParseRequired(
                target.ModelId,
                target.RuntimeConfigJson,
                _settingsSecretsOptions.CurrentValue);
        }
        catch (Exception ex)
        {
            throw new RoutingException(
                RoutingErrorCodes.ProviderNotReady,
                $"openai-compatible:baseUrl is not configured on model '{target.ModelId}'.",
                action: $"Open Settings → Models & Runtime → Catalog and set a Base URL for '{target.ModelId}'.",
                serviceId: "Chat",
                providerSection: "openai-compatible",
                modelId: target.ModelId,
                innerException: ex);
        }
        return _openAiCompatibleFactory.CreateClientForBehavior(
            target.ModelId,
            ToProviderChatBehavior(target.ChatBehavior),
            httpClient,
            new OpenAiCompatibleChatConfig
            {
                BaseUrl = config.BaseUrl,
                ApiKey = string.IsNullOrEmpty(config.ApiKey) ? null : config.ApiKey
            });
    }

    /// <summary>
    /// Builds the per-call LlamaCpp config for a row-owned stack (multi-stack
    /// llama-cpp). Returns null for rows that target the global LlamaCpp:BaseUrl —
    /// the factory's configured profile then applies, byte-identical to the
    /// pre-multi-stack behavior.
    /// </summary>
    private LlamaCppConfig? BuildLlamaCppConfigOverride(LocalRuntimeConfiguration localRuntime)
    {
        if (localRuntime.UsesGlobalStack)
        {
            return null;
        }

        var config = new LlamaCppConfig();
        _configuration.GetSection("LlamaCpp").Bind(config);
        // Row carries the stack root; the /llama-cpp prefix is a product decision
        // (same doctrine as LocalServiceAdminRouting for the other local services).
        config.BaseUrl = localRuntime.StackBaseUrl.TrimEnd('/') + "/llama-cpp";
        config.ApiKey = localRuntime.StackApiKey;
        return config;
    }

    private static LlamaCppRuntimeProfileData ToLlamaCppProfileData(RuntimeProfileData data)
    {
        var samplingDefaults = data.SamplingParameters
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Default);

        var thinkingControl = new AntRunner.Chat.LlamaCpp.ThinkingControl(
            data.ThinkingControl.DefaultChoice,
            data.ThinkingControl.ChoiceActions.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<AntRunner.Chat.LlamaCpp.ThinkingAction>)kvp.Value.Select(a =>
                    new AntRunner.Chat.LlamaCpp.ThinkingAction(
                        (AntRunner.Chat.LlamaCpp.ThinkingActionTarget)(int)a.Target,
                        a.Key,
                        a.Value)).ToList()));

        return new LlamaCppRuntimeProfileData(
            data.ProfileId,
            data.CombineSystemAndDeveloperMessages,
            data.ThoughtBlockPattern,
            samplingDefaults,
            thinkingControl,
            data.RequestFieldsWhenToolsPresent);
    }
}
