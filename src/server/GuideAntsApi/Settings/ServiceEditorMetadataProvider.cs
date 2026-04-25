using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Settings;

public interface IServiceEditorMetadataProvider
{
    IReadOnlyList<ProviderFieldMetadataDto> GetProviderFields(string serviceId, string providerId);
}

public sealed class ServiceEditorMetadataProvider : IServiceEditorMetadataProvider
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>> Metadata
        = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>>(StringComparer.Ordinal)
        {
            [RoutedServiceNames.Embeddings] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding] =
                [
                    Field("Endpoint", "Endpoint", "url", true, "Azure OpenAI embeddings endpoint.", operative: true),
                    Field("ApiKey", "API Key", "secret", true, "Credential for Azure OpenAI embeddings.", operative: true),
                    Field("Deployment", "Deployment", "text", true, "Deployment name for embeddings.", operative: true),
                ],
                [ServiceProviderIds.EmbeddingsLocalEmbHttp] =
                [
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Local request timeout in seconds.", operative: true),
                    Field("LocalMinIntervalMs", "Local Min Interval (ms)", "int", true, "Minimum pacing interval between local calls.", operative: true),
                ],
                [ServiceProviderIds.EmbeddingsGoogleEmbedding] =
                [
                    Field("ApiKey", "API Key", "secret", true, "Google Gemini API key.", operative: true),
                    Field("ModelId", "Embedding Model ID", "text", true, "Gemini embedding model id routed through ServiceModes.", operative: true),
                ],
                [ServiceProviderIds.EmbeddingsHuggingFaceInference] =
                [
                    Field("Token", "Token", "secret", true, "Hugging Face token for inference APIs.", operative: true),
                    Field("ModelId", "Embedding Model ID", "text", true, "Model id routed through ServiceModes for Hugging Face embeddings.", operative: true),
                ],
                [ServiceProviderIds.EmbeddingsOpenRouterEmbeddings] =
                [
                    Field("ApiKey", "API Key", "secret", true, "OpenRouter API key.", operative: true),
                    Field("BaseUrl", "Base URL", "url", false, "OpenRouter API base URL.", operative: true),
                    Field("ModelId", "Embedding Model ID", "text", true, "Model id routed through ServiceModes for OpenRouter embeddings.", operative: true),
                ]
            },
            [RoutedServiceNames.ImageGeneration] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.ImageGenerationAzureOpenAiImages] =
                [
                    Field("Endpoint", "Endpoint", "url", true, "Azure OpenAI image endpoint.", operative: true),
                    Field("ApiKey", "API Key", "secret", true, "Credential for image requests.", operative: true),
                    Field("Deployment", "Deployment", "text", true, "Deployment for generation.", operative: true),
                    Field("EditModelDeployment", "Edit Model Deployment", "text", true, "Deployment for edit requests.", operative: true),
                    Field("ApiVersion", "API Version", "text", false, "Azure API version override.", operative: true),
                ],
                [ServiceProviderIds.ImageGenerationLocalSdHttp] =
                [
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Local Stable Diffusion request timeout.", operative: true),
                    Field(
                        "LocalOutputFormat",
                        "Default output format",
                        "enum",
                        true,
                        "Local SD output: png, jpeg, or webp (jpg maps to jpeg).",
                        enumOptions: ["png", "jpeg", "webp"],
                        operative: true),
                    Field("modelId", "Model", "text", false, "Legacy mode model binding (non-operative).", operative: false),
                    Field("requestPresetJson", "Request Preset", "text", false, "Legacy mode request preset (non-operative).", operative: false),
                ],
                [ServiceProviderIds.ImageGenerationGoogleImagen] =
                [
                    Field("ApiKey", "API Key", "secret", true, "Google Gemini API key.", operative: true),
                    Field("ModelId", "Image Model ID", "text", true, "Gemini image-capable model id routed through ServiceModes.", operative: true),
                ],
                [ServiceProviderIds.ImageGenerationHuggingFaceInference] =
                [
                    Field("Token", "Token", "secret", true, "Hugging Face token for inference APIs.", operative: true),
                    Field("ModelId", "Image Model ID", "text", true, "Model id routed through ServiceModes for Hugging Face image generation.", operative: true),
                ],
                [ServiceProviderIds.ImageGenerationOpenRouterImage] =
                [
                    Field("ApiKey", "API Key", "secret", true, "OpenRouter API key.", operative: true),
                    Field("BaseUrl", "Base URL", "url", false, "OpenRouter API base URL.", operative: true),
                    Field("ModelId", "Image Model ID", "text", true, "Model id routed through ServiceModes for OpenRouter image generation.", operative: true),
                ]
            },
            [RoutedServiceNames.DocumentIntelligence] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.DocumentIntelligenceAzure] =
                [
                    Field("Endpoint", "Endpoint", "url", true, "Azure Document Intelligence endpoint.", operative: true),
                    Field("ApiKey", "API Key", "secret", true, "Credential for Azure Document Intelligence.", operative: true),
                    Field("ApiVersion", "API Version", "text", false, "Azure API version for extraction.", operative: true),
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Timeout for extraction calls.", operative: true),
                    Field("MaxRetries", "Max Retries", "int", true, "Retry attempts for transient failures.", operative: true),
                ],
                [ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp] =
                [
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Local conversion timeout.", operative: true),
                    Field("MaxConcurrentConversions", "Max Concurrent Conversions", "int", true, "Queue concurrency for local conversion.", operative: true),
                    Field("AsyncStatusPollIntervalMs", "Poll Interval (ms)", "int", true, "Status polling interval.", operative: true),
                    Field("DoclingDoOcr", "Do OCR", "enum", false, "Enable OCR when needed.", enumOptions: ["true", "false"], operative: true),
                    Field("DoclingForceOcr", "Force OCR", "enum", false, "Force OCR for all pages.", enumOptions: ["true", "false"], operative: true),
                    Field("DoclingOcrPreset", "OCR Preset", "text", false, "Docling OCR preset.", operative: true),
                    Field("DoclingPdfBackend", "PDF Backend", "text", false, "Docling PDF backend.", operative: true),
                    Field("DoclingTableMode", "Table Mode", "text", false, "Docling table extraction mode.", operative: true),
                    Field("DoclingTableCellMatching", "Table Cell Matching", "text", false, "Cell matching strategy.", operative: true),
                    Field("DoclingImageExportMode", "Image Export Mode", "text", false, "Docling image export mode.", operative: true),
                ]
            },
            [RoutedServiceNames.SpeechTranscription] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch] =
                [
                    Field("Endpoint", "Endpoint", "url", true, "Azure Speech endpoint.", operative: true),
                    Field("ApiKey", "API Key", "secret", true, "Credential for Azure Speech Batch.", operative: true),
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Cloud transcription timeout.", operative: true),
                    Field("language", "Language", "text", false, "Legacy request preset language.", operative: false),
                    Field("modelHint", "Model Hint", "text", false, "Legacy request preset model hint.", operative: false),
                ],
                [ServiceProviderIds.SpeechTranscriptionLocalAsrHttp] =
                [
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Local ASR timeout.", operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText] =
                [
                    Field("ApiKey", "API Key", "secret", true, "Google Gemini API key.", operative: true),
                    Field("ModelId", "Transcription Model ID", "text", true, "Gemini audio-capable model id routed through ServiceModes.", operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionHuggingFaceInference] =
                [
                    Field("Token", "Token", "secret", true, "Hugging Face token for inference APIs.", operative: true),
                    Field("ModelId", "ASR Model ID", "text", true, "Model id routed through ServiceModes for Hugging Face ASR.", operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionOpenRouterAudio] =
                [
                    Field("ApiKey", "API Key", "secret", true, "OpenRouter API key.", operative: true),
                    Field("BaseUrl", "Base URL", "url", false, "OpenRouter API base URL.", operative: true),
                    Field("ModelId", "Transcription Model ID", "text", true, "Model id routed through ServiceModes for OpenRouter transcription.", operative: true),
                ]
            },
            [RoutedServiceNames.SpeechSynthesis] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.SpeechSynthesisAzureSpeechSsml] =
                [
                    Field("ApiKey", "API Key", "secret", true, "Azure Speech credential.", operative: true),
                    Field("Region", "Region", "text", true, "Azure Speech region.", operative: true),
                    Field("Endpoint", "Endpoint", "url", false, "Optional endpoint override.", operative: true),
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Azure synthesis timeout.", operative: true),
                    Field("voice", "Voice", "text", false, "Legacy request preset voice.", operative: false),
                    Field("language", "Language", "text", false, "Legacy request preset language.", operative: false),
                    Field("rate", "Rate", "text", false, "Legacy request preset rate.", operative: false),
                    Field("modelId", "Model", "text", false, "Legacy mode model binding.", operative: false),
                ],
                [ServiceProviderIds.SpeechSynthesisLocalTtsHttp] =
                [
                    Field("TimeoutSeconds", "Timeout Seconds", "int", true, "Local TTS timeout.", operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisGoogleTextToSpeech] =
                [
                    Field("ApiKey", "API Key", "secret", true, "Google Gemini API key.", operative: true),
                    Field("ModelId", "TTS Model ID", "text", true, "Gemini TTS model id routed through ServiceModes.", operative: true),
                    Field("VoiceName", "Voice Name", "text", true, "Required Gemini prebuilt voice name stored in the service mode preset.", operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisHuggingFaceInference] =
                [
                    Field("Token", "Token", "secret", true, "Hugging Face token for inference APIs.", operative: true),
                    Field("ModelId", "TTS Model ID", "text", true, "Model id routed through ServiceModes for Hugging Face TTS.", operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisOpenRouterTts] =
                [
                    Field("ApiKey", "API Key", "secret", true, "OpenRouter API key.", operative: true),
                    Field("BaseUrl", "Base URL", "url", false, "OpenRouter API base URL.", operative: true),
                    Field("ModelId", "TTS Model ID", "text", true, "Model id routed through ServiceModes for OpenRouter TTS.", operative: true),
                ]
            },
        };

    public IReadOnlyList<ProviderFieldMetadataDto> GetProviderFields(string serviceId, string providerId)
    {
        if (!Metadata.TryGetValue(serviceId, out var providers))
        {
            return [];
        }

        return providers.TryGetValue(providerId, out var fields) ? fields : [];
    }

    private static ProviderFieldMetadataDto Field(
        string name,
        string label,
        string kind,
        bool required,
        string helpText,
        IReadOnlyList<string>? enumOptions = null,
        bool operative = true)
    {
        return new ProviderFieldMetadataDto(name, label, kind, required, helpText, enumOptions, operative);
    }
}
