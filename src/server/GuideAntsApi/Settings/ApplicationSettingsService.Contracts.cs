using GuideAntsApi.Options;

namespace GuideAntsApi.Settings;

public sealed partial class ApplicationSettingsService
{
    /// <summary>
    /// Minimum required field set per provider section, used by
    /// <see cref="GetProviderSectionReadinessAsync"/>. Fields that depend on the
    /// caller's service context (e.g. <c>AzureSpeechService</c> needs
    /// <c>Endpoint</c> for transcription but <c>Region</c> for synthesis) are
    /// intentionally NOT listed here — the mode-level probe adds per-service
    /// blockers on top of this common base.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProviderSectionRequiredFields =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AzureOpenAI"] = new[] { "Resource", "ApiKey", "Deployment" },
            ["OpenAI"] = new[] { "ApiKey" },
            ["Anthropic"] = new[] { "ApiKey", "AuthToken" },
            ["LlamaCpp"] = new[] { "BaseUrl" },
            ["AzureOpenAiEmbedding"] = new[] { "Endpoint", "ApiKey", "Deployment" },
            ["AzureOpenAiImages"] = new[] { "Endpoint", "ApiKey", "Deployment", "EditModelDeployment" },
            ["AzureSpeechService"] = new[] { "ApiKey" },
            ["AzureDocumentIntelligence"] = new[] { "Endpoint", "ApiKey" }
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
                    ])
            ],
            ErrorKeys:
            [
                "AzureSpeechService:Endpoint",
                "AzureSpeechService:ApiKey",
                "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                "LocalServiceHosts:MediaBaseUrl"
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
                    ])
            ],
            ErrorKeys:
            [
                "AzureSpeechService:ApiKey",
                "AzureSpeechService:Region",
                "LocalServiceHosts:SpeechSynthesisBaseUrl"
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
                    ])
            ],
            ErrorKeys:
            [
                "AzureOpenAiImages:Endpoint",
                "AzureOpenAiImages:ApiKey",
                "AzureOpenAiImages:Deployment",
                "AzureOpenAiImages:EditModelDeployment",
                "LocalServiceHosts:ImageGenerationBaseUrl"
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
                    ])
            ],
            ErrorKeys:
            [
                "AzureOpenAiEmbedding:Endpoint",
                "AzureOpenAiEmbedding:ApiKey",
                "AzureOpenAiEmbedding:Deployment",
                "LocalServiceHosts:EmbeddingsBaseUrl"
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
