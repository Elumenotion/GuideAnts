using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class ProviderRoutedEmbeddingService(
    AzureOpenAiEmbeddingService azureEmbeddingService,
    LocalEmbeddingService localEmbeddingService,
    IServiceModeResolver serviceModeResolver) : IEmbeddingService
{
    private const string AzureProviderSection = "AzureOpenAiEmbedding";
    private const string LocalProviderSection = "LocalServiceHosts:EmbeddingsBaseUrl";

    private readonly AzureOpenAiEmbeddingService _azureEmbeddingService = azureEmbeddingService;
    private readonly LocalEmbeddingService _localEmbeddingService = localEmbeddingService;
    private readonly IServiceModeResolver _serviceModeResolver = serviceModeResolver;

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var mode = await _serviceModeResolver
            .ResolveAsync(RoutedServiceNames.Embeddings, modeId: null, cancellationToken)
            .ConfigureAwait(false);

        return mode.ProviderSection switch
        {
            AzureProviderSection => await _azureEmbeddingService
                .GetEmbeddingsAsync(texts, purpose, cancellationToken)
                .ConfigureAwait(false),
            LocalProviderSection => await _localEmbeddingService
                .GetEmbeddingsAsync(texts, purpose, cancellationToken)
                .ConfigureAwait(false),
            _ => throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[]
                {
                    $"Embeddings mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                    $"Expected '{AzureProviderSection}' or '{LocalProviderSection}'."
                },
                serviceId: RoutedServiceNames.Embeddings,
                modeId: mode.ModeId)
        };
    }
}
