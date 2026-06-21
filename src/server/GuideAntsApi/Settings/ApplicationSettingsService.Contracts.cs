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
                RequiredFields: ["Resource", "ApiKey"],
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
                RequiredFields: ["Endpoint", "ApiKey"],
                AlternativeFieldGroups: []),
            ["AzureOpenAiImages"] = new(
                RequiredFields: ["Endpoint", "ApiKey"],
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
        string FieldName);

    private sealed record RuntimeKeyRequirement(
        string Key);

    private sealed record ProviderContract(
        string ProviderId,
        string ProviderKind,
        string ProviderSectionKey,
        string? ProviderSettingsSection,
        IReadOnlyList<SectionFieldRequirement> RequiredSectionFields,
        IReadOnlyList<RuntimeKeyRequirement> RequiredRuntimeKeys)
    {
        public string Kind => string.Equals(ProviderKind, "Cloud", StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
        public string? SectionName => ProviderSectionKey;
    }

    private sealed record ServiceContract(
        string ServiceId,
        string SectionName,
        IReadOnlyList<string> ServiceFieldNames,
        IReadOnlyList<ProviderContract> Providers,
        IReadOnlyList<string> ErrorKeys);

    private sealed record RuntimeDependencyContract(
        string Key,
        IReadOnlyList<string> UsedByProviderIds,
        bool ReadOnly = true);

    private static readonly IReadOnlyList<ServiceContract> ServiceContracts =
    [
        new(
            ServiceId: SpeechTranscriptionOptions.SectionName,
            SectionName: SpeechTranscriptionOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds", "MaxRetries"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: AzureSpeechServiceOptions.SectionName,
                    ProviderSettingsSection: null,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "Endpoint"),
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                    ProviderSettingsSection: SpeechTranscriptionOptions.SectionName,
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement("LocalServiceHosts:SpeechTranscriptionBaseUrl"),
                        new RuntimeKeyRequirement("LocalServiceHosts:MediaBaseUrl")
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionHuggingFaceInference,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionOpenRouterAudio,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechTranscriptionOpenAiAudio,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "OpenAI",
                    ProviderSettingsSection: "OpenAI",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("OpenAI", "ApiKey")
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
                "OpenRouter:ApiKey",
                "OpenAI:ApiKey"
            ]),
        new(
            ServiceId: SpeechSynthesisOptions.SectionName,
            SectionName: SpeechSynthesisOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds", "MaxRetries"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisAzureSpeechSsml,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: AzureSpeechServiceOptions.SectionName,
                    ProviderSettingsSection: null,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "ApiKey"),
                        new SectionFieldRequirement(AzureSpeechServiceOptions.SectionName, "Region")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:SpeechSynthesisBaseUrl",
                    ProviderSettingsSection: SpeechSynthesisOptions.SectionName,
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement("LocalServiceHosts:SpeechSynthesisBaseUrl")
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisGoogleTextToSpeech,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisHuggingFaceInference,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisOpenRouterTts,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.SpeechSynthesisOpenAiTts,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "OpenAI",
                    ProviderSettingsSection: "OpenAI",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("OpenAI", "ApiKey")
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
                "OpenRouter:ApiKey",
                "OpenAI:ApiKey"
            ]),
        new(
            ServiceId: ImageGenerationOptions.SectionName,
            SectionName: ImageGenerationOptions.SectionName,
            ServiceFieldNames: ["TimeoutSeconds", "LocalOutputFormat"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationAzureOpenAiImages,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "AzureOpenAiImages",
                    ProviderSettingsSection: null,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("AzureOpenAiImages", "Endpoint"),
                        new SectionFieldRequirement("AzureOpenAiImages", "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationLocalSdHttp,
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:ImageGenerationBaseUrl",
                    ProviderSettingsSection: ImageGenerationOptions.SectionName,
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement("LocalServiceHosts:ImageGenerationBaseUrl")
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationGoogleImagen,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationHuggingFaceInference,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationOpenRouterImage,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.ImageGenerationOpenAiImages,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "OpenAI",
                    ProviderSettingsSection: "OpenAI",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("OpenAI", "ApiKey")
                    ],
                    RequiredRuntimeKeys: [])
            ],
            ErrorKeys:
            [
                "AzureOpenAiImages:Endpoint",
                "AzureOpenAiImages:ApiKey",
                "LocalServiceHosts:ImageGenerationBaseUrl",
                "GoogleGeminiApi:ApiKey",
                "HuggingFace:Token",
                "OpenRouter:ApiKey",
                "OpenAI:ApiKey"
            ]),
        new(
            ServiceId: "Embeddings",
            SectionName: "Embeddings",
            ServiceFieldNames: ["TimeoutSeconds", "LocalMinIntervalMs"],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "AzureOpenAiEmbedding",
                    ProviderSettingsSection: null,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("AzureOpenAiEmbedding", "Endpoint"),
                        new SectionFieldRequirement("AzureOpenAiEmbedding", "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsLocalEmbHttp,
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:EmbeddingsBaseUrl",
                    ProviderSettingsSection: "Embeddings",
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement("LocalServiceHosts:EmbeddingsBaseUrl")
                    ]),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsGoogleEmbedding,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: GoogleGeminiApiOptions.SectionName,
                    ProviderSettingsSection: GoogleGeminiApiOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(GoogleGeminiApiOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsHuggingFaceInference,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "HuggingFace",
                    ProviderSettingsSection: "HuggingFace",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("HuggingFace", "Token")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsOpenRouterEmbeddings,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: OpenRouterOptions.SectionName,
                    ProviderSettingsSection: OpenRouterOptions.SectionName,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(OpenRouterOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.EmbeddingsOpenAiEmbedding,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: "OpenAI",
                    ProviderSettingsSection: "OpenAI",
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement("OpenAI", "ApiKey")
                    ],
                    RequiredRuntimeKeys: [])
            ],
            ErrorKeys:
            [
                "AzureOpenAiEmbedding:Endpoint",
                "AzureOpenAiEmbedding:ApiKey",
                "LocalServiceHosts:EmbeddingsBaseUrl",
                "GoogleGeminiApi:ApiKey",
                "HuggingFace:Token",
                "OpenRouter:ApiKey",
                "OpenAI:ApiKey"
            ]),
        new(
            ServiceId: DocumentIntelligenceOptions.SectionName,
            SectionName: DocumentIntelligenceOptions.SectionName,
            ServiceFieldNames:
            [
                "TimeoutSeconds",
                "MaxConcurrentConversions",
                "AsyncStatusPollIntervalMs",
                "DoclingDoOcr",
                "DoclingForceOcr",
                "DoclingOcrPreset",
                "DoclingPdfBackend",
                "DoclingTableMode",
                "DoclingTableCellMatching",
                "DoclingImageExportMode",
                "DoclingOcrLang",
                "DoclingPipeline",
                "DoclingDoCodeEnrichment",
                "DoclingDoFormulaEnrichment",
                "DoclingDoPictureClassification",
                "DoclingDoPictureDescription",
                "DoclingPictureDescriptionPreset",
                "DoclingApiKey"
            ],
            Providers:
            [
                new ProviderContract(
                    ProviderId: ServiceProviderIds.DocumentIntelligenceAzure,
                    ProviderKind: "Cloud",
                    ProviderSectionKey: AzureDocumentIntelligenceOptions.SectionName,
                    ProviderSettingsSection: null,
                    RequiredSectionFields:
                    [
                        new SectionFieldRequirement(AzureDocumentIntelligenceOptions.SectionName, "Endpoint"),
                        new SectionFieldRequirement(AzureDocumentIntelligenceOptions.SectionName, "ApiKey")
                    ],
                    RequiredRuntimeKeys: []),
                new ProviderContract(
                    ProviderId: ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp,
                    ProviderKind: "LocalHttp",
                    ProviderSectionKey: "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                    ProviderSettingsSection: DocumentIntelligenceOptions.SectionName,
                    RequiredSectionFields: [],
                    RequiredRuntimeKeys:
                    [
                        new RuntimeKeyRequirement("LocalServiceHosts:DocumentIntelligenceBaseUrl")
                    ])
            ],
            ErrorKeys:
            [
                "AzureDocumentIntelligence:Endpoint",
                "AzureDocumentIntelligence:ApiKey",
                "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                "DocumentIntelligence:DoclingApiKey"
            ])
    ];
}
