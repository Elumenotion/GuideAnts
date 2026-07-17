using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Curated catalog entry ids seeded into ServiceModes when a local provider mode
/// is first created. Warmup does not infer models from this table — it loads only
/// what is persisted on the active service mode.
/// </summary>
internal static class LocalAuxiliaryCatalogDefaults
{
    internal static string? TryGetDefaultCatalogModelId(string serviceId, string providerId)
    {
        if (string.Equals(serviceId, RoutedServiceNames.Embeddings, StringComparison.Ordinal)
            && string.Equals(providerId, ServiceProviderIds.EmbeddingsLocalEmbHttp, StringComparison.Ordinal))
        {
            return "qwen3_embedding_0_6b";
        }

        if (string.Equals(serviceId, RoutedServiceNames.SpeechTranscription, StringComparison.Ordinal)
            && string.Equals(providerId, ServiceProviderIds.SpeechTranscriptionLocalAsrHttp, StringComparison.Ordinal))
        {
            return "qwen3_asr_0_6b";
        }

        if (string.Equals(serviceId, RoutedServiceNames.SpeechSynthesis, StringComparison.Ordinal)
            && string.Equals(providerId, ServiceProviderIds.SpeechSynthesisLocalTtsHttp, StringComparison.Ordinal))
        {
            return "chatterbox";
        }

        if (string.Equals(serviceId, RoutedServiceNames.SpeechSynthesis, StringComparison.Ordinal)
            && string.Equals(providerId, ServiceProviderIds.SpeechSynthesisHuggingFaceInference, StringComparison.Ordinal))
        {
            return "ResembleAI/chatterbox";
        }

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
            && string.Equals(providerId, ServiceProviderIds.ImageGenerationLocalSdHttp, StringComparison.Ordinal))
        {
            return "flux2-klein-4b";
        }

        return null;
    }
}
