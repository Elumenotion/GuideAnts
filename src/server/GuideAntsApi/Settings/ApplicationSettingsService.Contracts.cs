using GuideAntsApi.Options;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    private sealed record ProviderSectionRequirement(
        IReadOnlyList<string> RequiredFields,
        IReadOnlyList<IReadOnlyList<string>> AlternativeFieldGroups);

    /// <summary>
    /// Minimum required field contract per provider section, used by
    /// <see cref="GetProviderSectionReadinessAsync"/> and <see cref="GetSchemaAsync"/>.
    /// Fields that depend on the caller's service context (e.g.
    /// <c>AzureSpeechService</c> needs <c>Endpoint</c> for transcription but
    /// <c>Region</c> for synthesis) are intentionally not listed here — the
    /// mode-level probe adds per-service blockers on top of this common base.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ProviderSectionRequirement> ProviderSectionRequirements =
        new Dictionary<string, ProviderSectionRequirement>(StringComparer.OrdinalIgnoreCase)
        {
            ["AzureOpenAI"] = new(
                RequiredFields: ["Resource", "ApiKey", "Deployment"],
                AlternativeFieldGroups: []),
            ["OpenAI"] = new(
                RequiredFields: ["ApiKey"],
                AlternativeFieldGroups: []),
            ["Anthropic"] = new(
                RequiredFields: [],
                AlternativeFieldGroups:
                [
                    new[] { "ApiKey", "AuthToken" }
                ]),
            ["LlamaCpp"] = new(
                RequiredFields: ["BaseUrl"],
                AlternativeFieldGroups: []),
            ["AzureOpenAiEmbedding"] = new(
                RequiredFields: ["Endpoint", "ApiKey", "Deployment"],
                AlternativeFieldGroups: []),
            ["AzureOpenAiImages"] = new(
                RequiredFields: ["Endpoint", "ApiKey", "Deployment", "EditModelDeployment"],
                AlternativeFieldGroups: []),
            ["AzureSpeechService"] = new(
                RequiredFields: ["ApiKey"],
                AlternativeFieldGroups: []),
            ["AzureDocumentIntelligence"] = new(
                RequiredFields: ["Endpoint", "ApiKey"],
                AlternativeFieldGroups: []),
            ["GoogleGeminiApi"] = new(
                RequiredFields: ["ApiKey"],
                AlternativeFieldGroups: []),
            ["OpenRouter"] = new(
                RequiredFields: ["ApiKey"],
                AlternativeFieldGroups: []),
            ["HuggingFace"] = new(
                RequiredFields: ["Token"],
                AlternativeFieldGroups: [])
        };

    private sealed record SectionFieldRequirement(
        string SectionName,
        string FieldName,
        string DisplayName);

    private sealed record RuntimeKeyRequirement(
        string Key,
        string DisplayName,
        string ChangeHint);

    private sealed record ProviderContract(
        string ProviderId,
        string ProviderDisplayName,
        string ProviderKind,
        string ProviderSectionKey,
        string? ProviderSettingsSection,
        string? MarketingSummary,
        IReadOnlyList<SectionFieldRequirement> RequiredSectionFields,
        IReadOnlyList<RuntimeKeyRequirement> RequiredRuntimeKeys)
    {
        // Backward-compatible aliases used by existing readiness/mode code.
        public string DisplayName => ProviderDisplayName;
        public string Kind => string.Equals(ProviderKind, "Cloud", StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
        public string? SectionName => ProviderSectionKey;
    }

    private sealed record ServiceContract(
        string ServiceId,
        string DisplayName,
        string SectionName,
        IReadOnlyList<string> ServiceFieldNames,
        IReadOnlyList<ProviderContract> Providers,
        IReadOnlyList<string> ErrorKeys);

    private sealed record RuntimeDependencyContract(
        string Key,
        string DisplayName,
        string ChangeHint,
        IReadOnlyList<string> UsedByProviderIds);

    private const string RuntimeChangeHint =
        "Runtime-owned value. Change in appsettings, environment variables, or docker-compose LocalServiceHosts__* settings.";

    private static readonly IReadOnlyList<ServiceContract> ServiceContracts =
    [
        new(
            ServiceId: SpeechTranscriptionOptions.SectionName,
            DisplayName: "Speech Transcription",
            SectionName: SpeechTranscriptionOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch,
                    ProviderDisplayName: "Azure Speech Batch",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: AzureSpeechServiceOptions.SectionName,
                    ProviderSettingsSection: null,
                    MarketingSummary: "Cloud batch transcription via Azure Speech.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "Endpoint", "Azure Speech Endpoint"),
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "ApiKey", "Azure Speech API Key")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
                    ProviderDisplayName: "Local ASR HTTP",
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                    ProviderSettingsSection: SpeechTranscriptionOptions.SectionName,
                    MarketingSummary: "Local ASR service for on-device transcription.",
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement(
                            "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                            "Speech Transcription Base URL",
                            RuntimeChangeHint),
                        new RuntimeKeyRequirement(
                            "LocalServiceHosts:MediaBaseUrl",
                            "Media Extraction Base URL",
                            RuntimeChangeHint)
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText,
                    ProviderDisplayName: "Google Gemini Audio",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    MarketingSummary: "Cloud transcription via the Google Gemini API.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey", "Google Gemini API Key")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionHuggingFaceInference,
                    ProviderDisplayName: "Hugging Face ASR",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    MarketingSummary: "Cloud transcription through Hugging Face inference APIs.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token", "Hugging Face Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionOpenRouterAudio,
                    ProviderDisplayName: "OpenRouter Audio",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    MarketingSummary: "Cloud transcription through OpenRouter chat audio input.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey", "OpenRouter API Key")
                    ],
                    RequiredRuntimeKeys: [])
            ],
            ErrorKeys:
            [
                "AzureSpeechService:Endpoint",
                "AzureSpeechService:ApiKey",
                "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                "LocalServiceHosts:MediaBaseUrl",
                "GoogleGeminiApi:ApiKey",
                "HuggingFace:Token",
                "OpenRouter:ApiKey"
            ]),
        new(
            ServiceId: SpeechSynthesisOptions.SectionName,
            DisplayName: "Speech Synthesis",
            SectionName: SpeechSynthesisOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisAzureSpeechSsml,
                    ProviderDisplayName: "Azure Speech SSML",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: AzureSpeechServiceOptions.SectionName,
                    ProviderSettingsSection: null,
                    MarketingSummary: "Cloud SSML synthesis through Azure Speech.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "ApiKey", "Azure Speech API Key"),
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "Region", "Azure Speech Region")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
                    ProviderDisplayName: "Local TTS HTTP",
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:SpeechSynthesisBaseUrl",
                    ProviderSettingsSection: SpeechSynthesisOptions.SectionName,
                    MarketingSummary: "Local text-to-speech runtime over HTTP.",
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement(
                            "LocalServiceHosts:SpeechSynthesisBaseUrl",
                            "Speech Synthesis Base URL",
                            RuntimeChangeHint)
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisGoogleTextToSpeech,
                    ProviderDisplayName: "Google Gemini TTS",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    MarketingSummary: "Cloud synthesis through the Google Gemini API.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey", "Google Gemini API Key")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisHuggingFaceInference,
                    ProviderDisplayName: "Hugging Face TTS",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    MarketingSummary: "Cloud synthesis through Hugging Face inference APIs.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token", "Hugging Face Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisOpenRouterTts,
                    ProviderDisplayName: "OpenRouter TTS",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    MarketingSummary: "Cloud synthesis through OpenRouter TTS endpoint.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey", "OpenRouter API Key")
                    ],
                    RequiredRuntimeKeys: [])
            ],
            ErrorKeys:
            [
                "AzureSpeechService:ApiKey",
                "AzureSpeechService:Region",
                "LocalServiceHosts:SpeechSynthesisBaseUrl",
                "GoogleGeminiApi:ApiKey",
                "HuggingFace:Token",
                "OpenRouter:ApiKey"
            ]),
        new(
            ServiceId: ImageGenerationOptions.SectionName,
            DisplayName: "Image Generation",
            SectionName: ImageGenerationOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationAzureOpenAiImages,
                    ProviderDisplayName: "Azure OpenAI Images",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "AzureOpenAiImages",
                    ProviderSettingsSection: null,
                    MarketingSummary: "Cloud image generation via Azure OpenAI.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("AzureOpenAiImages", "Endpoint", "Azure OpenAI Images Endpoint"),
                        new SectionFieldRequirement("AzureOpenAiImages", "ApiKey", "Azure OpenAI Images API Key"),
                        new SectionFieldRequirement("AzureOpenAiImages", "Deployment", "Image Deployment"),
                        new SectionFieldRequirement("AzureOpenAiImages", "EditModelDeployment", "Image Edit Deployment")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationLocalSdHttp,
                    ProviderDisplayName: "Local Stable Diffusion HTTP",
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:ImageGenerationBaseUrl",
                    ProviderSettingsSection: ImageGenerationOptions.SectionName,
                    MarketingSummary: "Local Stable Diffusion runtime over HTTP.",
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement(
                            "LocalServiceHosts:ImageGenerationBaseUrl",
                            "Image Generation Base URL",
                            RuntimeChangeHint)
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationGoogleImagen,
                    ProviderDisplayName: "Google Gemini Image",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    MarketingSummary: "Cloud image generation via the Google Gemini API.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey", "Google Gemini API Key")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationHuggingFaceInference,
                    ProviderDisplayName: "Hugging Face Image",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    MarketingSummary: "Cloud image generation through Hugging Face task APIs.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token", "Hugging Face Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationOpenRouterImage,
                    ProviderDisplayName: "OpenRouter Image",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    MarketingSummary: "Cloud image generation through OpenRouter image-capable chat endpoints.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey", "OpenRouter API Key")
                    ],
                    RequiredRuntimeKeys: [])
            ],
            ErrorKeys:
            [
                "AzureOpenAiImages:Endpoint",
                "AzureOpenAiImages:ApiKey",
                "AzureOpenAiImages:Deployment",
                "AzureOpenAiImages:EditModelDeployment",
                "LocalServiceHosts:ImageGenerationBaseUrl",
                "GoogleGeminiApi:ApiKey",
                "HuggingFace:Token",
                "OpenRouter:ApiKey"
            ]),
        new(
            ServiceId: "Embeddings",
            DisplayName: "Embeddings",
            SectionName: "Embeddings",
            ServiceFieldNames: ["TimeoutSeconds", "LocalMinIntervalMs"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
                    ProviderDisplayName: "Azure OpenAI Embedding",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "AzureOpenAiEmbedding",
                    ProviderSettingsSection: null,
                    MarketingSummary: "Cloud embedding generation via Azure OpenAI.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("AzureOpenAiEmbedding", "Endpoint", "Azure OpenAI Embedding Endpoint"),
                        new SectionFieldRequirement("AzureOpenAiEmbedding", "ApiKey", "Azure OpenAI Embedding API Key"),
                        new SectionFieldRequirement("AzureOpenAiEmbedding", "Deployment", "Embedding Deployment")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsLocalEmbHttp,
                    ProviderDisplayName: "Local Embedding HTTP",
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:EmbeddingsBaseUrl",
                    ProviderSettingsSection: "Embeddings",
                    MarketingSummary: "Local embedding service over HTTP.",
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement(
                            "LocalServiceHosts:EmbeddingsBaseUrl",
                            "Embeddings Base URL",
                            RuntimeChangeHint)
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsGoogleEmbedding,
                    ProviderDisplayName: "Google Gemini Embeddings",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    MarketingSummary: "Cloud embeddings through the Google Gemini API.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey", "Google Gemini API Key")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsHuggingFaceInference,
                    ProviderDisplayName: "Hugging Face Embeddings",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    MarketingSummary: "Cloud embeddings through Hugging Face feature-extraction APIs.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token", "Hugging Face Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsOpenRouterEmbeddings,
                    ProviderDisplayName: "OpenRouter Embeddings",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    MarketingSummary: "Cloud embeddings through OpenRouter embeddings endpoint.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey", "OpenRouter API Key")
                    ],
                    RequiredRuntimeKeys: [])
            ],
            ErrorKeys:
            [
                "AzureOpenAiEmbedding:Endpoint",
                "AzureOpenAiEmbedding:ApiKey",
                "AzureOpenAiEmbedding:Deployment",
                "LocalServiceHosts:EmbeddingsBaseUrl",
                "GoogleGeminiApi:ApiKey",
                "HuggingFace:Token",
                "OpenRouter:ApiKey"
            ]),
        new(
            ServiceId: DocumentIntelligenceOptions.SectionName,
            DisplayName: "Markdown Extraction",
            SectionName: DocumentIntelligenceOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds", "MaxConcurrentConversions", "AsyncStatusPollIntervalMs"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.DocumentIntelligenceAzure,
                    ProviderDisplayName: "Azure Document Intelligence",
                    ProviderKind: "Cloud",
                    ProviderSectionKey: AzureDocumentIntelligenceOptions.SectionName,
                    ProviderSettingsSection: null,
                    MarketingSummary: "Cloud markdown extraction via Azure Document Intelligence.",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(AzureDocumentIntelligenceOptions.SectionName, "Endpoint", "Azure Document Intelligence Endpoint"),
                        new SectionFieldRequirement(AzureDocumentIntelligenceOptions.SectionName, "ApiKey", "Azure Document Intelligence API Key")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp,
                    ProviderDisplayName: "Local Docling HTTP",
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                    ProviderSettingsSection: DocumentIntelligenceOptions.SectionName,
                    MarketingSummary: "Local Docling service for markdown extraction.",
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement(
                            "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                            "Markdown Extraction Base URL",
                            RuntimeChangeHint)
                    ])
            ],
            ErrorKeys:
            [
                "AzureDocumentIntelligence:Endpoint",
                "AzureDocumentIntelligence:ApiKey",
                "LocalServiceHosts:DocumentIntelligenceBaseUrl"
            ])
    ];
}
