using System.Text.Json.Nodes;

namespace GuideAntsApi.Settings;

public enum SettingsValueType
{
    String,
    Int,
    Bool
}

public sealed record SettingsPropertyDefinition(
    string Name,
    string CanonicalKey,
    SettingsValueType ValueType = SettingsValueType.String,
    bool IsSecret = false,
    object? DefaultValue = null);

public sealed class SettingsSectionDefinition
{
    public required string SectionName { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public required IReadOnlyList<SettingsPropertyDefinition> Properties { get; init; }

    /// <summary>
    /// When set, <see cref="BuildBootstrapPayload"/> uses this instead of mapping flat <see cref="Properties"/>.
    /// Used for nested JSON (e.g. <c>ServiceRouting:Containers:*:BaseUrl</c>) that must match DB shape.
    /// </summary>
    public Func<IConfiguration, JsonObject>? BootstrapPayloadFactory { get; init; }

    public bool HasSecrets => Properties.Any(p => p.IsSecret);

    public JsonObject BuildBootstrapPayload(IConfiguration configuration)
    {
        if (BootstrapPayloadFactory is not null)
        {
            return BootstrapPayloadFactory(configuration);
        }

        var payload = new JsonObject();
        foreach (var property in Properties)
        {
            var raw = ReadFromConfiguration(configuration, property);
            var node = ParseRawValue(property, raw, fallbackToDefault: true);
            if (node != null)
            {
                payload[property.Name] = node;
            }
        }

        return payload;
    }

    public IReadOnlyList<string> Validate(JsonObject payload)
    {
        var errors = new List<string>();

        foreach (var property in Properties)
        {
            if (!payload.TryGetPropertyValue(property.Name, out var node) || node is null)
            {
                continue;
            }

            switch (property.ValueType)
            {
                case SettingsValueType.Int:
                    if (node.GetValueKind() == System.Text.Json.JsonValueKind.String
                        && !int.TryParse(node.GetValue<string>(), out _))
                    {
                        errors.Add($"{property.Name} must be an integer.");
                    }
                    else if (node.GetValueKind() != System.Text.Json.JsonValueKind.Number
                             && node.GetValueKind() != System.Text.Json.JsonValueKind.String)
                    {
                        errors.Add($"{property.Name} must be an integer.");
                    }
                    break;

                case SettingsValueType.Bool:
                    if (node.GetValueKind() == System.Text.Json.JsonValueKind.String
                        && !bool.TryParse(node.GetValue<string>(), out _))
                    {
                        errors.Add($"{property.Name} must be true or false.");
                    }
                    else if (node.GetValueKind() != System.Text.Json.JsonValueKind.True
                             && node.GetValueKind() != System.Text.Json.JsonValueKind.False
                             && node.GetValueKind() != System.Text.Json.JsonValueKind.String)
                    {
                        errors.Add($"{property.Name} must be true or false.");
                    }
                    break;
            }
        }

        AddPositiveIntegerValidation(payload, errors, "TimeoutSeconds");
        AddPositiveIntegerValidation(payload, errors, "MaxConcurrentConversions");
        AddPositiveIntegerValidation(payload, errors, "AsyncStatusPollIntervalMs");
        AddPositiveIntegerValidation(payload, errors, "PollIntervalSeconds");
        AddPositiveIntegerValidation(payload, errors, "PollTimeoutSeconds");
        AddPositiveIntegerValidation(payload, errors, "MaxRetries");
        AddPositiveIntegerValidation(payload, errors, "MaxAudioBytes");

        return errors;
    }

    public string? GetCanonicalKey(string propertyName)
    {
        var property = Properties.FirstOrDefault(p =>
            p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        return property?.CanonicalKey;
    }

    private static string? ReadFromConfiguration(IConfiguration configuration, SettingsPropertyDefinition property)
    {
        var raw = configuration[property.CanonicalKey];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        return null;
    }

    private static JsonNode? ParseRawValue(SettingsPropertyDefinition property, string? raw, bool fallbackToDefault)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!fallbackToDefault)
            {
                return null;
            }

            return property.DefaultValue switch
            {
                null => null,
                int i => JsonValue.Create(i),
                bool b => JsonValue.Create(b),
                _ => JsonValue.Create(property.DefaultValue?.ToString())
            };
        }

        return property.ValueType switch
        {
            SettingsValueType.Int => int.TryParse(raw, out var i)
                ? JsonValue.Create(i)
                : JsonValue.Create(raw),
            SettingsValueType.Bool => bool.TryParse(raw, out var b)
                ? JsonValue.Create(b)
                : JsonValue.Create(raw),
            _ => JsonValue.Create(raw)
        };
    }

    private static void AddPositiveIntegerValidation(JsonObject payload, List<string> errors, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return;
        }

        int? value = node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
            System.Text.Json.JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var i) => i,
            _ => null
        };

        if (value.HasValue && value.Value <= 0)
        {
            errors.Add($"{propertyName} must be greater than 0.");
        }
    }
}

public interface ISettingsSectionRegistry
{
    IReadOnlyList<SettingsSectionDefinition> All { get; }
    bool TryGet(string sectionName, out SettingsSectionDefinition definition);
}

public sealed class SettingsSectionRegistry : ISettingsSectionRegistry
{
    public const string TelemetrySectionName = "Telemetry";

    private static JsonObject BuildTelemetryBootstrapPayload(IConfiguration _)
    {
        var payload = new JsonObject();
        foreach (var property in TelemetryProperties)
        {
            payload[property.Name] = JsonValue.Create(property.DefaultValue?.ToString());
        }

        return payload;
    }

    public static readonly IReadOnlyList<SettingsPropertyDefinition> TelemetryProperties =
    [
        new("Default", "Logging:LogLevel:Default", DefaultValue: "Warning"),
        new("MicrosoftAspNetCore", "Logging:LogLevel:Microsoft.AspNetCore", DefaultValue: "Warning"),
        new("MicrosoftEntityFrameworkCore", "Logging:LogLevel:Microsoft.EntityFrameworkCore", DefaultValue: "Warning"),
        new("MicrosoftEntityFrameworkCoreDatabaseCommand", "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", DefaultValue: "Warning"),
        new("MicrosoftEntityFrameworkCoreQuery", "Logging:LogLevel:Microsoft.EntityFrameworkCore.Query", DefaultValue: "Warning"),
        new("Program", "Logging:LogLevel:Program", DefaultValue: "Information"),
        new("GuideAntsApi", "Logging:LogLevel:GuideAntsApi", DefaultValue: "Information"),
        new("GuideAntsApiBackgroundJobs", "Logging:LogLevel:GuideAntsApi.BackgroundJobs", DefaultValue: "Information"),
        new("GuideAntsApiBackgroundJobHandlers", "Logging:LogLevel:GuideAntsApi.BackgroundJobs.Jobs", DefaultValue: "Information"),
        new("DocumentIntelligenceService", "Logging:LogLevel:GuideAntsApi.BackgroundJobs.Services.DocumentIntelligenceService", DefaultValue: "Information"),
        new("EmbeddingServices", "Logging:LogLevel:GuideAntsApi.BackgroundJobs.Services.Embeddings", DefaultValue: "Information"),
        new("IndexingServices", "Logging:LogLevel:GuideAntsApi.BackgroundJobs.Services.Indexing", DefaultValue: "Information"),
        new("SearchServices", "Logging:LogLevel:GuideAntsApi.BackgroundJobs.Services.Search", DefaultValue: "Information"),
        new("GuideAntsApiServicesRouting", "Logging:LogLevel:GuideAntsApi.Services.Routing", DefaultValue: "Information"),
        new("RoutingChatCompletionClientFactory", "Logging:LogLevel:GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory", DefaultValue: "Information"),
        new("GuideAntsApiServicesLlamaCpp", "Logging:LogLevel:GuideAntsApi.Services.LlamaCpp", DefaultValue: "Information"),
        new("InfrastructureProbeService", "Logging:LogLevel:GuideAntsApi.Services.Infrastructure.InfrastructureProbeService", DefaultValue: "Warning"),
        new("SpeechTranscriptionService", "Logging:LogLevel:GuideAntsApi.Services.Components.SpeechTranscriptionService", DefaultValue: "Information"),
        new("SpeechSynthesisService", "Logging:LogLevel:GuideAntsApi.Services.Components.SpeechSynthesisService", DefaultValue: "Information"),
        new("VideoAudioExtractionService", "Logging:LogLevel:GuideAntsApi.Services.Components.VideoAudioExtractionService", DefaultValue: "Information"),
        new("NotebookImageService", "Logging:LogLevel:GuideAntsApi.Services.NotebookImageService", DefaultValue: "Information"),
        new("SearXngWebSearchService", "Logging:LogLevel:GuideAntsApi.Services.SearXngWebSearchService", DefaultValue: "Information"),
        new("SearXngBrowserRenderingClient", "Logging:LogLevel:GuideAntsApi.Services.SearXngBrowserRenderingClient", DefaultValue: "Warning"),
        new("StoragePathResolver", "Logging:LogLevel:GuideAntsApi.Services.StoragePathResolver", DefaultValue: "Warning"),
        new("NotebookFileService", "Logging:LogLevel:GuideAntsApi.Services.Components.NotebookFileService", DefaultValue: "Warning"),
        new("NotebookFileSyncService", "Logging:LogLevel:GuideAntsApi.Services.Components.NotebookFileSyncService", DefaultValue: "Warning"),
        new("ContentFileService", "Logging:LogLevel:GuideAntsApi.Services.Components.ContentFileService", DefaultValue: "Warning"),
        new("GuideAntsUsage", "Logging:LogLevel:GuideAnts.Usage", DefaultValue: "Warning"),
        new("AntRunner", "Logging:LogLevel:AntRunner", DefaultValue: "Warning"),
        new("AntRunnerChat", "Logging:LogLevel:AntRunner.Chat", DefaultValue: "Warning"),
        new("AntRunnerChatLlamaCpp", "Logging:LogLevel:AntRunner.Chat.LlamaCpp", DefaultValue: "Warning")
    ];

    public IReadOnlyList<SettingsSectionDefinition> All { get; } =
    [
        new()
        {
            SectionName = TelemetrySectionName,
            BootstrapPayloadFactory = BuildTelemetryBootstrapPayload,
            Properties = TelemetryProperties
        },
        new()
        {
            SectionName = "AzureDocumentIntelligence",
            Properties =
            [
                new("Endpoint", "AzureDocumentIntelligence:Endpoint"),
                new("ApiKey", "AzureDocumentIntelligence:ApiKey", IsSecret: true)
            ]
        },
        new()
        {
            SectionName = "DocumentIntelligence",
            Properties =
            [
                new("TimeoutSeconds", "DocumentIntelligence:TimeoutSeconds", SettingsValueType.Int, DefaultValue: 300),
                new("MaxConcurrentConversions", "DocumentIntelligence:MaxConcurrentConversions", SettingsValueType.Int, DefaultValue: 1),
                new("AsyncStatusPollIntervalMs", "DocumentIntelligence:AsyncStatusPollIntervalMs", SettingsValueType.Int, DefaultValue: 2000),
                new("DoclingDoOcr", "DocumentIntelligence:DoclingDoOcr", SettingsValueType.Bool),
                new("DoclingForceOcr", "DocumentIntelligence:DoclingForceOcr", SettingsValueType.Bool),
                new("DoclingOcrPreset", "DocumentIntelligence:DoclingOcrPreset"),
                new("DoclingPdfBackend", "DocumentIntelligence:DoclingPdfBackend"),
                new("DoclingTableMode", "DocumentIntelligence:DoclingTableMode"),
                new("DoclingTableCellMatching", "DocumentIntelligence:DoclingTableCellMatching"),
                new("DoclingImageExportMode", "DocumentIntelligence:DoclingImageExportMode"),
                new("DoclingOcrLang", "DocumentIntelligence:DoclingOcrLang"),
                new("DoclingPipeline", "DocumentIntelligence:DoclingPipeline"),
                new("DoclingDoCodeEnrichment", "DocumentIntelligence:DoclingDoCodeEnrichment", SettingsValueType.Bool),
                new("DoclingDoFormulaEnrichment", "DocumentIntelligence:DoclingDoFormulaEnrichment", SettingsValueType.Bool),
                new("DoclingDoPictureClassification", "DocumentIntelligence:DoclingDoPictureClassification", SettingsValueType.Bool),
                new("DoclingDoPictureDescription", "DocumentIntelligence:DoclingDoPictureDescription", SettingsValueType.Bool),
                new("DoclingPictureDescriptionPreset", "DocumentIntelligence:DoclingPictureDescriptionPreset"),
                new("DoclingApiKey", "DocumentIntelligence:DoclingApiKey", IsSecret: true)
            ]
        },
        new()
        {
            SectionName = "AzureSpeechService",
            Properties =
            [
                new("Endpoint", "AzureSpeechService:Endpoint"),
                new("ApiKey", "AzureSpeechService:ApiKey", IsSecret: true),
                new("Region", "AzureSpeechService:Region", DefaultValue: "eastus")
            ]
        },
        new()
        {
            SectionName = "SpeechTranscription",
            Properties =
            [
                new("TimeoutSeconds", "SpeechTranscription:TimeoutSeconds", SettingsValueType.Int, DefaultValue: 300),
                new("MaxRetries", "SpeechTranscription:MaxRetries", SettingsValueType.Int, DefaultValue: 3)
            ]
        },
        new()
        {
            SectionName = "SpeechSynthesis",
            Properties =
            [
                new("TimeoutSeconds", "SpeechSynthesis:TimeoutSeconds", SettingsValueType.Int, DefaultValue: 300),
                new("MaxRetries", "SpeechSynthesis:MaxRetries", SettingsValueType.Int, DefaultValue: 3)
            ]
        },
        new()
        {
            SectionName = "AzureOpenAI",
            Properties =
            [
                new("Resource", "AzureOpenAI:Resource"),
                new("ApiKey", "AzureOpenAI:ApiKey", IsSecret: true),
                new("ApiVersion", "AzureOpenAI:ApiVersion", DefaultValue: "2025-04-01-preview")
            ]
        },
        new()
        {
            SectionName = "AzureOpenAiImages",
            Properties =
            [
                new("Endpoint", "AzureOpenAiImages:Endpoint"),
                new("ApiKey", "AzureOpenAiImages:ApiKey", IsSecret: true),
                new("ApiVersion", "AzureOpenAiImages:ApiVersion", DefaultValue: "2025-04-01-preview")
            ]
        },
        new()
        {
            SectionName = "ImageGeneration",
            Properties =
            [
                new("TimeoutSeconds", "ImageGeneration:TimeoutSeconds", SettingsValueType.Int, DefaultValue: 600),
                new("LocalOutputFormat", "ImageGeneration:LocalOutputFormat", DefaultValue: "png")
            ]
        },
        new()
        {
            SectionName = "Embeddings",
            Properties =
            [
                new("TimeoutSeconds", "Embeddings:TimeoutSeconds", SettingsValueType.Int, DefaultValue: 300),
                new("LocalMinIntervalMs", "Embeddings:LocalMinIntervalMs", SettingsValueType.Int, DefaultValue: 5000)
            ]
        },
        new()
        {
            SectionName = "AzureOpenAiEmbedding",
            Properties =
            [
                new("Endpoint", "AzureOpenAiEmbedding:Endpoint"),
                new("ApiKey", "AzureOpenAiEmbedding:ApiKey", IsSecret: true)
            ]
        },
        new()
        {
            SectionName = "OpenAI",
            Properties =
            [
                new("ApiKey", "OpenAI:ApiKey", IsSecret: true),
                new("Endpoint", "OpenAI:Endpoint")
            ]
        },
        new()
        {
            SectionName = "GoogleGeminiApi",
            Properties =
            [
                new("ApiKey", "GoogleGeminiApi:ApiKey", IsSecret: true)
            ]
        },
        new()
        {
            SectionName = "Anthropic",
            Properties =
            [
                new("BaseUrl", "Anthropic:BaseUrl"),
                new("ApiKey", "Anthropic:ApiKey", IsSecret: true),
                new("AuthToken", "Anthropic:AuthToken", IsSecret: true)
            ]
        },
        new()
        {
            SectionName = "OpenRouter",
            Properties =
            [
                new("ApiKey", "OpenRouter:ApiKey", IsSecret: true),
                new("BaseUrl", "OpenRouter:BaseUrl", DefaultValue: "https://openrouter.ai/api/v1")
            ]
        },
        // Single source of truth for the Hugging Face token used by every HF
        // download path in the app (llama quants + mmproj, Stable Diffusion
        // bundles, Whisper ASR, Kokoro TTS). Stored encrypted in the DB and
        // edited on Settings → Connections → Hugging Face.
        new()
        {
            SectionName = "HuggingFace",
            Properties =
            [
                new(
                    "Token",
                    "HuggingFace:Token",
                    IsSecret: true),
                new(
                    "RouterBaseUrl",
                    "HuggingFace:RouterBaseUrl",
                    DefaultValue: "https://router.huggingface.co/v1")
            ]
        },
        new()
        {
            SectionName = "LlamaCpp",
            Properties =
            [
                new("BaseUrl", "LlamaCpp:BaseUrl")
            ]
        },
        new()
        {
            SectionName = "LocalServiceHosts",
            Properties =
            [
                new("SpeechTranscriptionBaseUrl", "LocalServiceHosts:SpeechTranscriptionBaseUrl"),
                new("SpeechSynthesisBaseUrl", "LocalServiceHosts:SpeechSynthesisBaseUrl"),
                new("ImageGenerationBaseUrl", "LocalServiceHosts:ImageGenerationBaseUrl"),
                new("EmbeddingsBaseUrl", "LocalServiceHosts:EmbeddingsBaseUrl"),
                new("MediaBaseUrl", "LocalServiceHosts:MediaBaseUrl"),
                new("DocumentIntelligenceBaseUrl", "LocalServiceHosts:DocumentIntelligenceBaseUrl")
            ]
        },
        // Non-chat routing mode matrix (R-1.3). Chat is deliberately NOT represented here —
        // chat routing is assistant-driven (R-1.1). The Modes property is a JSON object keyed
        // by service name ("Embeddings", "ImageGeneration", "SpeechTranscription",
        // "SpeechSynthesis", "DocumentIntelligence"); each value carries a defaultModeId plus
        // a list of ServiceMode rows. Persisted as a JSON blob so the schema can evolve
        // without a new section property per service.
        new()
        {
            SectionName = "ServiceModes",
            Properties =
            [
                new("Modes", "ServiceModes:Modes")
            ]
        },
        // Global default chat model (R-9). Not a ServiceModes matrix row — chat stays assistant-driven
        // except when OverrideAllChatModels forces this catalog model id for every turn.
        new()
        {
            SectionName = "ChatDefaults",
            Properties =
            [
                new("DefaultModelId", "ChatDefaults:DefaultModelId"),
                new("OverrideAllChatModels", "ChatDefaults:OverrideAllChatModels", SettingsValueType.Bool, DefaultValue: false),
                new("Temperature", "ChatDefaults:Temperature"),
                new("TopP", "ChatDefaults:TopP"),
                new("ReasoningEffort", "ChatDefaults:ReasoningEffort"),
                new("SamplingParametersJson", "ChatDefaults:SamplingParametersJson")
            ]
        },
        new()
        {
            SectionName = GuideAntsSystemSettings.SectionName,
            Properties =
            [
                new("projectId", "GuideAntsSystem:ProjectId"),
                new("userGuideId", "GuideAntsSystem:UserGuideId"),
                new("adminGuideId", "GuideAntsSystem:AdminGuideId"),
                new("userNotebookId", "GuideAntsSystem:UserNotebookId"),
                new("adminNotebookId", "GuideAntsSystem:AdminNotebookId"),
                new("userPublishedGuideId", "GuideAntsSystem:UserPublishedGuideId"),
                new("adminPublishedGuideId", "GuideAntsSystem:AdminPublishedGuideId"),
                new(
                    "clientBridgeId",
                    "GuideAntsSystem:ClientBridgeId",
                    DefaultValue: GuideAntsSystemSettings.DefaultClientBridgeId)
            ]
        }
    ];

    public bool TryGet(string sectionName, out SettingsSectionDefinition definition)
    {
        definition = All.FirstOrDefault(x => x.SectionName.Equals(sectionName, StringComparison.OrdinalIgnoreCase))
                     ?? null!;
        return definition != null;
    }
}
