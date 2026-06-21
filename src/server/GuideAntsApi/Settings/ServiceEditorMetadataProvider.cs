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
    private static readonly IReadOnlyList<string> KokoroVoiceNames =
    [
        "af_heart",
        "af_alloy",
        "af_aoede",
        "af_bella",
        "af_jessica",
        "af_kore",
        "af_nicole",
        "af_nova",
        "af_river",
        "af_sarah",
        "af_sky",
        "am_adam",
        "am_echo",
        "am_eric",
        "am_fenrir",
        "am_liam",
        "am_michael",
        "am_onyx",
        "am_puck",
        "am_santa",
        "bf_alice",
        "bf_emma",
        "bf_isabella",
        "bf_lily",
        "bm_daniel",
        "bm_fable",
        "bm_george",
        "bm_lewis",
        "jf_alpha",
        "jf_gongitsune",
        "jf_nezumi",
        "jf_tebukuro",
        "jm_kumo",
        "zf_xiaobei",
        "zf_xiaoni",
        "zf_xiaoxiao",
        "zf_xiaoyi",
        "zm_yunjian",
        "zm_yunxi",
        "zm_yunxia",
        "zm_yunyang",
        "ef_dora",
        "em_alex",
        "em_santa",
        "ff_siwis",
        "hf_alpha",
        "hf_beta",
        "hm_omega",
        "hm_psi",
        "if_sara",
        "im_nicola",
        "pf_dora",
        "pm_alex",
        "pm_santa",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>> Metadata
        = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>>(StringComparer.Ordinal)
        {
            [RoutedServiceNames.Embeddings] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding] =
                [
                    Field("Deployment", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.EmbeddingsLocalEmbHttp] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("LocalMinIntervalMs", "int", true, operative: true),
                ],
                [ServiceProviderIds.EmbeddingsGoogleEmbedding] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.EmbeddingsHuggingFaceInference] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.EmbeddingsOpenRouterEmbeddings] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.EmbeddingsOpenAiEmbedding] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("Dimensions", "int", false, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ]
            },
            [RoutedServiceNames.ImageGeneration] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.ImageGenerationAzureOpenAiImages] =
                [
                    Field("Deployment", "text", true, operative: true),
                    Field("EditModelDeployment", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.ImageGenerationLocalSdHttp] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("LocalOutputFormat", "enum", true,
                        enumOptions: ["png", "jpeg", "webp"],
                        operative: true),
                    Field("modelId", "text", false, operative: false),
                    Field("requestPresetJson", "text", false, operative: false),
                ],
                [ServiceProviderIds.ImageGenerationGoogleImagen] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.ImageGenerationHuggingFaceInference] =
                [
                    Field("TextToImageModelId", "text", true, operative: true),
                    Field("ImageToImageModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.ImageGenerationOpenRouterImage] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.ImageGenerationOpenAiImages] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ]
            },
            [RoutedServiceNames.DocumentIntelligence] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.DocumentIntelligenceAzure] =
                [
                    Field("ApiVersion", "text", false, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("MaxRetries", "int", true, operative: true),
                ],
                [ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("MaxConcurrentConversions", "int", true, operative: true),
                    Field("AsyncStatusPollIntervalMs", "int", true, operative: true),
                    Field("DoclingDoOcr", "enum", false, enumOptions: ["true", "false"], operative: true),
                    Field("DoclingForceOcr", "enum", false, enumOptions: ["true", "false"], operative: true),
                    Field("DoclingOcrPreset", "text", false, operative: true),
                    Field("DoclingPdfBackend", "text", false, operative: true),
                    Field("DoclingTableMode", "text", false, operative: true),
                    Field("DoclingTableCellMatching", "text", false, operative: true),
                    Field("DoclingImageExportMode", "text", false, operative: true),
                    Field("DoclingOcrLang", "text", false, operative: true),
                    Field("DoclingPipeline", "text", false, operative: true),
                    Field("DoclingDoCodeEnrichment", "enum", false, enumOptions: ["true", "false"], operative: true),
                    Field("DoclingDoFormulaEnrichment", "enum", false, enumOptions: ["true", "false"], operative: true),
                    Field("DoclingDoPictureClassification", "enum", false, enumOptions: ["true", "false"], operative: true),
                    Field("DoclingDoPictureDescription", "enum", false, enumOptions: ["true", "false"], operative: true),
                    Field("DoclingPictureDescriptionPreset", "text", false, operative: true),
                    Field("DoclingApiKey", "secret", false, operative: true),
                ]
            },
            [RoutedServiceNames.SpeechTranscription] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("MaxRetries", "int", false, operative: true),
                    Field("language", "text", false, operative: false),
                    Field("modelHint", "text", false, operative: false),
                ],
                [ServiceProviderIds.SpeechTranscriptionLocalAsrHttp] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionHuggingFaceInference] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionOpenRouterAudio] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("MaxAudioBytes", "int", false, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechTranscriptionOpenAiAudio] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ]
            },
            [RoutedServiceNames.SpeechSynthesis] = new Dictionary<string, IReadOnlyList<ProviderFieldMetadataDto>>(StringComparer.Ordinal)
            {
                [ServiceProviderIds.SpeechSynthesisAzureSpeechSsml] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("MaxRetries", "int", false, operative: true),
                    Field("voice", "text", false, operative: false),
                    Field("language", "text", false, operative: false),
                    Field("rate", "text", false, operative: false),
                    Field("modelId", "text", false, operative: false),
                ],
                [ServiceProviderIds.SpeechSynthesisLocalTtsHttp] =
                [
                    Field("TimeoutSeconds", "int", true, operative: true),
                    Field("VoiceName", "enum", false, enumOptions: KokoroVoiceNames, operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisGoogleTextToSpeech] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("VoiceName", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisHuggingFaceInference] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisOpenRouterTts] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("VoiceName", "text", false, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
                ],
                [ServiceProviderIds.SpeechSynthesisOpenAiTts] =
                [
                    Field("ModelId", "text", true, operative: true),
                    Field("VoiceName", "text", true, operative: true),
                    Field("TimeoutSeconds", "int", true, operative: true),
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
        string kind,
        bool required,
        IReadOnlyList<string>? enumOptions = null,
        bool operative = true)
    {
        return new ProviderFieldMetadataDto(name, kind, required, enumOptions, operative);
    }
}
