using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class ProviderRoutedEmbeddingService(
    AzureOpenAiEmbeddingService azureEmbeddingService,
    LocalEmbeddingService localEmbeddingService,
    GoogleGeminiEmbeddingService googleGeminiEmbeddingService,
    HuggingFaceEmbeddingService huggingFaceEmbeddingService,
    OpenRouterEmbeddingService openRouterEmbeddingService,
    OpenAiEmbeddingService openAiEmbeddingService,
    IServiceModeResolver serviceModeResolver) : IEmbeddingService
{
    private const string AzureProviderSection = "AzureOpenAiEmbedding";
    private const string LocalProviderSection = "LocalServiceHosts:EmbeddingsBaseUrl";
    private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";
    private const string OpenAiProviderSection = "OpenAI";

    private readonly AzureOpenAiEmbeddingService _azureEmbeddingService = azureEmbeddingService;
    private readonly LocalEmbeddingService _localEmbeddingService = localEmbeddingService;
    private readonly GoogleGeminiEmbeddingService _googleGeminiEmbeddingService = googleGeminiEmbeddingService;
    private readonly HuggingFaceEmbeddingService _huggingFaceEmbeddingService = huggingFaceEmbeddingService;
    private readonly OpenRouterEmbeddingService _openRouterEmbeddingService = openRouterEmbeddingService;
    private readonly OpenAiEmbeddingService _openAiEmbeddingService = openAiEmbeddingService;
    private readonly IServiceModeResolver _serviceModeResolver = serviceModeResolver;

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var mode = await _serviceModeResolver
            .ResolveAsync(RoutedServiceNames.Embeddings, modeId: null, cancellationToken)
            .ConfigureAwait(false);

        var vectors = mode.ProviderSection switch
        {
            AzureProviderSection => await _azureEmbeddingService
                .GetEmbeddingsAsync(texts, purpose, cancellationToken)
                .ConfigureAwait(false),
            LocalProviderSection => await _localEmbeddingService
                .GetEmbeddingsAsync(texts, purpose, cancellationToken)
                .ConfigureAwait(false),
            GoogleGeminiProviderSection => await _googleGeminiEmbeddingService
                .GetEmbeddingsAsync(texts, purpose, RequireModelId(mode), cancellationToken)
                .ConfigureAwait(false),
            HuggingFaceProviderSection => await _huggingFaceEmbeddingService
                .GetEmbeddingsAsync(texts, RequireModelId(mode), mode.RequestPresetJson, cancellationToken)
                .ConfigureAwait(false),
            OpenRouterProviderSection => await _openRouterEmbeddingService
                .GetEmbeddingsAsync(texts, RequireModelId(mode), mode.RequestPresetJson, cancellationToken)
                .ConfigureAwait(false),
            OpenAiProviderSection => await _openAiEmbeddingService
                .GetEmbeddingsAsync(texts, RequireModelId(mode), mode.RequestPresetJson, cancellationToken)
                .ConfigureAwait(false),
            _ => throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[]
                {
                    $"Embeddings mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                    $"Expected '{AzureProviderSection}', '{LocalProviderSection}', '{GoogleGeminiProviderSection}', '{HuggingFaceProviderSection}', '{OpenRouterProviderSection}', or '{OpenAiProviderSection}'."
                },
                serviceId: RoutedServiceNames.Embeddings,
                modeId: mode.ModeId)
        };

        return EmbeddingVectorDimensions.NormalizeBatchToTarget(vectors);
    }

    private static string RequireModelId(ServiceMode mode)
    {
        if (!string.IsNullOrWhiteSpace(mode.ModelId))
        {
            return mode.ModelId!;
        }

        throw RoutingException.ProviderNotReady(
            mode.ProviderSection,
            new[]
            {
                $"Embeddings mode '{mode.ModeId}' requires a model id for provider section '{mode.ProviderSection}'."
            },
            serviceId: RoutedServiceNames.Embeddings,
            modeId: mode.ModeId);
    }
}
