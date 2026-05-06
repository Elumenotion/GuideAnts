using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using AntRunner.Chat.GoogleGemini;
using AntRunner.Chat.HuggingFace;
using AntRunner.Chat.LlamaCpp;
using AntRunner.Chat.OpenAI;
using AntRunner.Chat.OpenRouter;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Services.Conversations;

public sealed class RoutingChatCompletionClientFactory : IChatCompletionClientFactory
{
    // DI keys for the four OpenAI-family factory registrations. Each key pairs
    // with the provider section it draws credentials from: "openai-platform"
    // reads OpenAI:*, "azure-openai" reads AzureOpenAI:*. Catalog rows are
    // dispatched to exactly one of these based on the Provider column.
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
    private readonly LlamaCppChatClientFactory _llamaCppFactory;
    private readonly IChatTargetResolver _chatTargetResolver;
    private readonly IChatTargetValidator _chatTargetValidator;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;
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
        LlamaCppChatClientFactory llamaCppFactory,
        IChatTargetResolver chatTargetResolver,
        IChatTargetValidator chatTargetValidator,
        IRuntimeProfileResolver runtimeProfileResolver,
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
        _llamaCppFactory = llamaCppFactory ?? throw new ArgumentNullException(nameof(llamaCppFactory));
        _chatTargetResolver = chatTargetResolver ?? throw new ArgumentNullException(nameof(chatTargetResolver));
        _chatTargetValidator = chatTargetValidator ?? throw new ArgumentNullException(nameof(chatTargetValidator));
        _runtimeProfileResolver = runtimeProfileResolver ?? throw new ArgumentNullException(nameof(runtimeProfileResolver));
        _logger = logger ?? NullLogger<RoutingChatCompletionClientFactory>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        // Chat dispatch is assistant-driven (R-1.1). Resolution + validation both fail-fast
        // with a RoutingException; there is no provider fallback (R-1.7, R-9.1).
        var target = _chatTargetResolver.Resolve(deploymentId);
        _chatTargetValidator.Validate(target);

        var provider = ParseProvider(target);
        _logger.LogInformation(
            "Chat provider route resolved. RequestedModelId={RequestedModelId}, CatalogModelId={CatalogModelId}, Provider={Provider}",
            string.IsNullOrWhiteSpace(deploymentId) ? "(unset)" : deploymentId,
            target.ModelId,
            target.Provider);

        if (provider == Provider.LlamaCpp)
        {
            var localRuntime = LocalRuntimeConfigurationParser.ParseRequired(target.ModelId, target.RuntimeConfigJson);
            var profileData = _runtimeProfileResolver
                .ResolveAsync(localRuntime.RuntimeProfileId)
                .GetAwaiter().GetResult();

            var llamaProfile = ToLlamaCppProfileData(profileData);

            return _llamaCppFactory.CreateClientForProfile(
                localRuntime.RouterModelId,
                llamaProfile,
                localRuntime.ParallelToolCalls,
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
            Provider.HuggingFaceInferenceChat => _huggingFaceFactory.CreateClient(target.ModelId, httpClient),
            Provider.OpenRouterChat => _openRouterFactory.CreateClient(target.ModelId, httpClient),
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
        _ => throw new RoutingException(
            RoutingErrorCodes.ProviderNotReady,
            $"Provider '{target.Provider}' is not supported. Expected openai-chat, openai-responses, azure-openai-chat, azure-openai-responses, anthropic, llama-cpp, google-gemini-chat, hf-inference-chat, or openrouter-chat.",
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
        OpenRouterChat
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
            thinkingControl);
    }
}
