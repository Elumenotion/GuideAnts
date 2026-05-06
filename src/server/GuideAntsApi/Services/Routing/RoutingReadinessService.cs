using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Routing;

public sealed class RoutingReadinessService : IRoutingReadinessService
{
    /// <summary>
    /// Stable blocker prefixes that the UI can key off without parsing human text
    /// (R-9.2 philosophy — codes, not messages). The prefix plus a colon keeps
    /// the message human-readable for logs while still being filterable.
    /// </summary>
    public static class BlockerKeys
    {
        public const string ProviderMissing = "PROVIDER_MISSING_FIELDS";
        public const string ModelMissing = "MODEL_NOT_FOUND";
        public const string ModelInactive = "MODEL_INACTIVE";
        public const string RuntimeState = "RUNTIME_STATE";
        public const string RuntimeArtifactMissing = "RUNTIME_ARTIFACT_MISSING";
        public const string RuntimeUnregistered = "RUNTIME_UNREGISTERED";
        public const string InvalidLocalRuntimeJson = "MODEL_LOCAL_RUNTIME_INVALID";
    }

    private readonly IApplicationSettingsService _settings;
    private readonly ILlamaRuntimeInventoryService _inventory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoutingReadinessService> _logger;

    public RoutingReadinessService(
        IApplicationSettingsService settings,
        ILlamaRuntimeInventoryService inventory,
        IServiceScopeFactory scopeFactory,
        ILogger<RoutingReadinessService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ModeReadinessDto> ProbeModeAsync(string service, string modeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service name is required.", nameof(service));
        }

        if (string.IsNullOrWhiteSpace(modeId))
        {
            throw new ArgumentException("Mode id is required.", nameof(modeId));
        }

        var modes = await _settings.GetServiceModesAsync(service, cancellationToken).ConfigureAwait(false);
        var mode = modes.FirstOrDefault(m => string.Equals(m.ModeId, modeId, StringComparison.Ordinal));
        if (mode == null)
        {
            return new ModeReadinessDto(
                Service: service,
                ModeId: modeId,
                Status: "blocked",
                Blockers: new[] { $"{RoutingErrorCodes.ModeNotFound}: service '{service}' has no mode '{modeId}'." },
                ProviderSection: null,
                ModelId: null,
                RuntimeState: null);
        }

        var blockers = new List<string>();

        var providerSection = mode.ProviderSection;
        if (!string.IsNullOrWhiteSpace(providerSection))
        {
            var providerReadiness = await _settings
                .GetProviderSectionReadinessAsync(providerSection, cancellationToken)
                .ConfigureAwait(false);
            if (!providerReadiness.Configured)
            {
                foreach (var field in providerReadiness.MissingFields)
                {
                    blockers.Add($"{BlockerKeys.ProviderMissing}: {providerSection}:{field} is not configured.");
                }
            }

            var additional = AdditionalServiceFieldBlockers(service, providerSection);
            blockers.AddRange(additional);
        }
        else
        {
            blockers.Add($"{BlockerKeys.ProviderMissing}: mode '{modeId}' has no provider section.");
        }

        if (RequiresExplicitModelId(service, mode.ProviderSection) && string.IsNullOrWhiteSpace(mode.ModelId))
        {
            blockers.Add(
                $"{BlockerKeys.ModelMissing}: mode '{mode.ModeId}' for service '{service}' and provider '{mode.ProviderSection}' requires a model id.");
        }

        blockers.AddRange(AdditionalModeFieldBlockers(service, mode));

        string? runtimeState = null;

        if (!string.IsNullOrWhiteSpace(mode.ModelId))
        {
            var (catalogRow, catalogBlockers) = await LookupCatalogRowAsync(mode.ModelId!, cancellationToken)
                .ConfigureAwait(false);
            blockers.AddRange(catalogBlockers);
            blockers.AddRange(ModelCapabilityBlockers(service, mode, mode.ModelId!));

            if (catalogRow != null
                && string.Equals(mode.ProviderSection, "LlamaCpp", StringComparison.OrdinalIgnoreCase))
            {
                var llamaResult = await ProbeLlamaRuntimeAsync(catalogRow, cancellationToken).ConfigureAwait(false);
                runtimeState = llamaResult.RuntimeState;
                blockers.AddRange(llamaResult.Blockers);
            }
        }

        return new ModeReadinessDto(
            Service: service,
            ModeId: mode.ModeId,
            Status: blockers.Count == 0 ? "ready" : "blocked",
            Blockers: blockers,
            ProviderSection: mode.ProviderSection,
            ModelId: mode.ModelId,
            RuntimeState: runtimeState);
    }

    public async Task<ChatTargetReadinessDto> ProbeChatTargetAsync(
        string modelId,
        CancellationToken cancellationToken = default,
        string referenceKind = "direct")
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model id is required.", nameof(modelId));
        }

        if (string.IsNullOrWhiteSpace(referenceKind))
        {
            referenceKind = "direct";
        }

        var blockers = new List<string>();
        string? runtimeState = null;

        var (catalogRow, catalogBlockers) = await LookupCatalogRowAsync(modelId, cancellationToken)
            .ConfigureAwait(false);
        blockers.AddRange(catalogBlockers);

        var provider = catalogRow?.Provider?.Trim() ?? string.Empty;

        if (catalogRow != null)
        {
            var providerSection = MapChatProviderToSection(provider);
            if (providerSection == null)
            {
                blockers.Add(
                    $"{BlockerKeys.ProviderMissing}: provider '{provider}' for model '{modelId}' is not a recognized chat provider.");
            }
            else
            {
                var providerReadiness = await _settings
                    .GetProviderSectionReadinessAsync(providerSection, cancellationToken)
                    .ConfigureAwait(false);
                if (!providerReadiness.Configured)
                {
                    foreach (var field in providerReadiness.MissingFields)
                    {
                        blockers.Add($"{BlockerKeys.ProviderMissing}: {providerSection}:{field} is not configured.");
                    }
                }
            }

            if (string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
            {
                var llamaResult = await ProbeLlamaRuntimeAsync(catalogRow, cancellationToken).ConfigureAwait(false);
                runtimeState = llamaResult.RuntimeState;
                blockers.AddRange(llamaResult.Blockers);
            }
        }

        var usageCount = await CountActiveAssistantUsageAsync(modelId, cancellationToken).ConfigureAwait(false);

        return new ChatTargetReadinessDto(
            ModelId: modelId,
            Provider: provider,
            Status: blockers.Count == 0 ? "ready" : "blocked",
            Blockers: blockers,
            RuntimeState: runtimeState,
            AssistantUsageCount: usageCount,
            ReferenceKind: referenceKind);
    }

    /// <summary>
    /// Maps a chat catalog row's <c>Provider</c> column to the provider section
    /// whose readiness drives this target's blockers. The set of accepted
    /// provider values MUST stay in lockstep with
    /// <c>IChatTargetValidator.KnownProviders</c>; any provider the validator
    /// accepts but this switch does not recognize will surface as a spurious
    /// <c>PROVIDER_MISSING_FIELDS</c> on a working chat path.
    /// <para>
    /// Each catalog string targets exactly one provider section:
    /// OpenAI-platform strings (<c>"openai-chat"</c> / <c>"openai-responses"</c>)
    /// read <c>OpenAI:*</c>; Azure-prefixed strings
    /// (<c>"azure-openai-chat"</c> / <c>"azure-openai-responses"</c>) read
    /// <c>AzureOpenAI:*</c>. Dispatch choice (Chat Completions vs Responses)
    /// is a runtime concern, but platform-vs-Azure is a credentials concern
    /// — the two must not cross.
    /// </para>
    /// </summary>
    internal static string? MapChatProviderToSection(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            "openai-chat" => "OpenAI",
            "openai-responses" => "OpenAI",
            "azure-openai-chat" => "AzureOpenAI",
            "azure-openai-responses" => "AzureOpenAI",
            "anthropic" => "Anthropic",
            "llama-cpp" => "LlamaCpp",
            "google-gemini-chat" => "GoogleGeminiApi",
            "hf-inference-chat" => "HuggingFace",
            "openrouter-chat" => "OpenRouter",
            _ => null
        };

    /// <summary>
    /// AzureSpeechService is used by two different services with different field
    /// requirements. <see cref="IApplicationSettingsService.GetProviderSectionReadinessAsync"/>
    /// reports only the common <c>ApiKey</c> requirement; the per-service extras
    /// (<c>Endpoint</c> for transcription, <c>Region</c> for synthesis) are layered
    /// on here so the mode-level readiness stays accurate.
    /// </summary>
    private IReadOnlyList<string> AdditionalServiceFieldBlockers(string service, string providerSection)
    {
        if (!string.Equals(providerSection, "AzureSpeechService", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        if (string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(_configurationForSpeech("Endpoint")))
        {
            return new[] { $"{BlockerKeys.ProviderMissing}: AzureSpeechService:Endpoint is not configured." };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(_configurationForSpeech("Region")))
        {
            return new[] { $"{BlockerKeys.ProviderMissing}: AzureSpeechService:Region is not configured." };
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> AdditionalModeFieldBlockers(string service, ServiceModeDto mode)
    {
        if (!string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        if (!string.Equals(mode.ProviderSection, "GoogleGeminiApi", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode.ProviderSection, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        if (TryReadServiceModePresetField(mode.RequestPresetJson, "VoiceName") is { Length: > 0 })
        {
            return Array.Empty<string>();
        }

        return
        [
            $"{BlockerKeys.ModelMissing}: mode '{mode.ModeId}' for service '{service}' and provider '{mode.ProviderSection}' requires VoiceName in RequestPresetJson."
        ];
    }

    private static string? TryReadServiceModePresetField(string? requestPresetJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(requestPresetJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestPresetJson);
            if (document.RootElement.TryGetProperty(fieldName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool RequiresExplicitModelId(string service, string providerSection)
    {
        if (string.IsNullOrWhiteSpace(providerSection))
        {
            return false;
        }

        if (string.Equals(providerSection, "GoogleGeminiApi", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(providerSection, "HuggingFace", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerSection, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(providerSection, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private IReadOnlyList<string> ModelCapabilityBlockers(string service, ServiceModeDto mode, string modelId)
    {
        var providerSection = mode.ProviderSection;
        if (string.IsNullOrWhiteSpace(providerSection) || string.IsNullOrWhiteSpace(modelId))
        {
            return Array.Empty<string>();
        }

        if (string.Equals(providerSection, "HuggingFace", StringComparison.OrdinalIgnoreCase))
        {
            return HuggingFaceCapabilityBlockers(service, modelId, mode.RequestPresetJson);
        }

        if (string.Equals(providerSection, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            return OpenRouterCapabilityBlockers(service, modelId, mode.RequestPresetJson);
        }

        if (string.Equals(providerSection, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiCapabilityBlockers(service, modelId, mode.RequestPresetJson);
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> HuggingFaceCapabilityBlockers(string service, string modelId, string? requestPresetJson)
    {
        if (string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("embed", StringComparison.OrdinalIgnoreCase)
                    || m.StartsWith("sentence-transformers/", StringComparison.OrdinalIgnoreCase)
                    || m.StartsWith("intfloat/", StringComparison.OrdinalIgnoreCase)
                    || m.StartsWith("BAAI/", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as embedding-capable for HuggingFace."
                };
        }

        if (string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("diffusion", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("flux", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as image-capable for HuggingFace."
                };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("asr", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("wav2vec", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as ASR-capable for HuggingFace."
                };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("tts", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("speech", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("kokoro", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as TTS-capable for HuggingFace."
                };
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> OpenRouterCapabilityBlockers(string service, string modelId, string? requestPresetJson)
    {
        if (string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("embed", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as embedding-capable for OpenRouter."
                };
        }

        if (string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("imagen", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("flux", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as image-capable for OpenRouter."
                };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("transcribe", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("audio", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as transcription-capable for OpenRouter."
                };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("tts", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("audio", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("speech", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as TTS-capable for OpenRouter."
                };
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> OpenAiCapabilityBlockers(string service, string modelId, string? requestPresetJson)
    {
        if (string.Equals(service, RoutedServiceNames.Embeddings, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("embed", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as embedding-capable for OpenAI."
                };
        }

        if (string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("dall-e", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("gpt-image", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as image-capable for OpenAI."
                };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechTranscription, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("transcribe", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as transcription-capable for OpenAI."
                };
        }

        if (string.Equals(service, RoutedServiceNames.SpeechSynthesis, StringComparison.OrdinalIgnoreCase))
        {
            return IsAllowedByConfigOrHeuristic(
                modelId,
                ReadServiceModePresetField(requestPresetJson, "AllowedModels"),
                m => m.Contains("tts", StringComparison.OrdinalIgnoreCase))
                ? Array.Empty<string>()
                : new[]
                {
                    $"{BlockerKeys.ModelMissing}: model '{modelId}' is not recognized as TTS-capable for OpenAI."
                };
        }

        return Array.Empty<string>();
    }

    private bool IsAllowedByConfigOrHeuristic(string modelId, string? allowlistCsv, Func<string, bool> heuristic)
    {
        if (!string.IsNullOrWhiteSpace(allowlistCsv))
        {
            var entries = allowlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                if (entry.EndsWith('*'))
                {
                    var prefix = entry[..^1];
                    if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (string.Equals(modelId, entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        return heuristic(modelId);
    }

    private static string? ReadServiceModePresetField(string? requestPresetJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(requestPresetJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestPresetJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(fieldName, out var node))
            {
                return null;
            }

            return node.ValueKind == JsonValueKind.String
                ? node.GetString()?.Trim()
                : node.ToString().Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? _configurationForSection(string section, string field)
    {
        using var scope = _scopeFactory.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        return configuration[$"{section}:{field}"];
    }

    /// <summary>
    /// Reads <c>AzureSpeechService:{field}</c> from the application DI scope's
    /// configuration. We pull from the scoped <see cref="IConfiguration"/> so
    /// that live-updates via the settings service propagate without restart.
    /// </summary>
    private string? _configurationForSpeech(string field)
    {
        using var scope = _scopeFactory.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        return configuration[$"AzureSpeechService:{field}"];
    }

    private async Task<(CatalogRow? Row, IReadOnlyList<string> Blockers)> LookupCatalogRowAsync(string modelId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == modelId)
            .Select(m => new CatalogRow(m.ModelId, m.Provider, m.IsActive, m.RuntimeConfigJson))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row == null)
        {
            return (null, new[] { $"{BlockerKeys.ModelMissing}: catalog model '{modelId}' was not found." });
        }

        if (!row.IsActive)
        {
            return (row, new[] { $"{BlockerKeys.ModelInactive}: catalog model '{modelId}' is inactive." });
        }

        return (row, Array.Empty<string>());
    }

    private async Task<(string? RuntimeState, IReadOnlyList<string> Blockers)> ProbeLlamaRuntimeAsync(CatalogRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
        {
            return (null, new[]
            {
                $"{BlockerKeys.InvalidLocalRuntimeJson}: model '{row.ModelId}' is configured as llama-cpp but has no RuntimeConfigJson."
            });
        }

        LocalRuntimeConfiguration parsed;
        try
        {
            parsed = LocalRuntimeConfigurationParser.ParseRequired(row.ModelId, row.RuntimeConfigJson);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RuntimeConfigJson parse failed for model {ModelId} during readiness probe.", row.ModelId);
            return (null, new[]
            {
                $"{BlockerKeys.InvalidLocalRuntimeJson}: model '{row.ModelId}' has invalid RuntimeConfigJson: {ex.Message}"
            });
        }

        var inventory = await _inventory.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var entry = inventory.FirstOrDefault(i => string.Equals(i.RouterModelId, parsed.RouterModelId, StringComparison.Ordinal));

        if (entry == null)
        {
            return (null, new[]
            {
                $"{BlockerKeys.RuntimeUnregistered}: router alias '{parsed.RouterModelId}' for model '{row.ModelId}' is not registered."
            });
        }

        var blockers = new List<string>();

        // RUNTIME_ARTIFACT_MISSING fires only when we have no evidence an artifact
        // exists anywhere: router has no path declaration AND the live llama-server
        // runtime does not report the alias loaded. The API does not probe host
        // filesystem paths for GGUF files (models live on the runtime); inventory
        // HasModelFile is informational (from router/admin), not a host File.Exists.
        var routerDeclaresPath = !string.IsNullOrWhiteSpace(entry.ModelPath);
        var runtimeIsLoaded = string.Equals(entry.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);

        if (!routerDeclaresPath && !runtimeIsLoaded)
        {
            blockers.Add(
                $"{BlockerKeys.RuntimeArtifactMissing}: alias '{parsed.RouterModelId}' has no router-declared model path and is not reported by llama-server.");
        }

        if (!runtimeIsLoaded)
        {
            blockers.Add(
                $"{BlockerKeys.RuntimeState}: alias '{parsed.RouterModelId}' runtime state is '{entry.RuntimeState}' (expected 'loaded').");
        }

        return (entry.RuntimeState, blockers);
    }

    private async Task<int> CountActiveAssistantUsageAsync(string modelId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId == modelId)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record CatalogRow(string ModelId, string Provider, bool IsActive, string? RuntimeConfigJson);
}
