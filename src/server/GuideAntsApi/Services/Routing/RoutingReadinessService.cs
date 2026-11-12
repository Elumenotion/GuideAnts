using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.OpenAiCompatible;
using GuideAntsApi.Services.Reachability;
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

        /// <summary>
        /// Emitted ONLY on the aux (local-service) branch in NonBlocking when the
        /// endpoint was observed unreachable. Never emitted on the llama/chat branch:
        /// a non-whitelisted blocker on a chat row would hide it from the selectable
        /// list (ShouldExposeChatModelOption). F-4 boundary; enforced by T-U7.
        /// </summary>
        public const string EndpointUnreachable = "ENDPOINT_UNREACHABLE";
    }

    private readonly IApplicationSettingsService _settings;
    private readonly ILlamaRuntimeInventoryService _inventory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILocalServiceLoadService? _loadService;
    private readonly GuideAntsApi.Services.LlamaCpp.ILlamaStackRuntimeClientProvider? _stackClients;
    // Plane C (reachability + last-known inventory). Optional so the Live path and
    // every existing construction/test behave exactly as today; NonBlocking reads them.
    private readonly ILocalEndpointCache? _endpointCache;
    private readonly ILocalInventoryMirror? _inventoryMirror;
    private readonly Bootstrap.IServiceLocalModelInstallationStore? _installStore;
    private readonly ILogger<RoutingReadinessService> _logger;

    public RoutingReadinessService(
        IApplicationSettingsService settings,
        ILlamaRuntimeInventoryService inventory,
        IServiceScopeFactory scopeFactory,
        ILogger<RoutingReadinessService> logger,
        ILocalServiceLoadService? loadService = null,
        GuideAntsApi.Services.LlamaCpp.ILlamaStackRuntimeClientProvider? stackClients = null,
        ILocalEndpointCache? endpointCache = null,
        ILocalInventoryMirror? inventoryMirror = null,
        Bootstrap.IServiceLocalModelInstallationStore? installStore = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loadService = loadService;
        _stackClients = stackClients;
        _endpointCache = endpointCache;
        _inventoryMirror = inventoryMirror;
        _installStore = installStore;
    }

    public async Task<ModeReadinessDto> ProbeModeAsync(
        string service,
        string modeId,
        CancellationToken cancellationToken = default,
        RoutingProbeMode probeMode = RoutingProbeMode.Live)
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
                if (probeMode == RoutingProbeMode.NonBlocking)
                {
                    // No inline network call (S-2). Runtime state from the Plane C alias mirror;
                    // no RUNTIME_ARTIFACT_MISSING / ENDPOINT_UNREACHABLE on the chat side here —
                    // this is a routed-service LlamaCpp mode, surfaced via cache only (F-4).
                    runtimeState = ReadCachedLlamaRuntimeState();
                }
                else
                {
                    var llamaResult = await ProbeLlamaRuntimeAsync(catalogRow, cancellationToken).ConfigureAwait(false);
                    runtimeState = llamaResult.RuntimeState;
                    blockers.AddRange(llamaResult.Blockers);
                }
            }
        }

        var auxBlockers = await ProbeWarmupOrchestratorBlockersAsync(service, mode, probeMode, cancellationToken).ConfigureAwait(false);
        blockers.AddRange(auxBlockers);

        // F-2 on the aux surface: under NonBlocking the mode's RuntimeState comes from the
        // Plane C cache (shown, never awaited). Live keeps its historical shape — it never
        // populated RuntimeState for aux modes and must stay byte-for-byte.
        DateTime? runtimeObservedUtc = null;
        bool runtimeObserved = false;
        if (probeMode == RoutingProbeMode.NonBlocking && IsLocalServiceProviderSection(mode.ProviderSection ?? string.Empty))
        {
            var cached = TryReadAuxObservation(service);
            if (cached is not null)
            {
                runtimeState = cached.Value.Observation.RuntimeState;
                runtimeObserved = true;
                runtimeObservedUtc = cached.Value.Observation.ObservedUtc;
            }
        }

        return new ModeReadinessDto(
            Service: service,
            ModeId: mode.ModeId,
            Status: blockers.Count == 0 ? "ready" : "blocked",
            Blockers: blockers,
            ProviderSection: mode.ProviderSection,
            ModelId: mode.ModelId,
            RuntimeState: runtimeState,
            RuntimeObserved: runtimeObserved,
            RuntimeObservedUtc: runtimeObservedUtc);
    }

    /// <summary>
    /// Plane C observation for one aux service's readiness URL (cache read only). Null when
    /// the service is not configured locally or nothing has been observed yet.
    /// </summary>
    private (EndpointObservation Observation, string ReadinessUrl)? TryReadAuxObservation(string service)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(service, ConfigurationForScope());
        if (string.IsNullOrWhiteSpace(adminBase) || _endpointCache is null)
        {
            return null;
        }

        var readinessUrl = $"{adminBase.TrimEnd('/')}{(string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal) ? "/health" : "/ready")}";
        return _endpointCache.TryGet(readinessUrl, out var observation)
            ? (observation, readinessUrl)
            : null;
    }

    public async Task<ChatTargetReadinessDto> ProbeChatTargetAsync(
        string modelId,
        CancellationToken cancellationToken = default,
        string referenceKind = "direct",
        RoutingProbeMode mode = RoutingProbeMode.Live)
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
                if (string.Equals(provider, "openai-compatible", StringComparison.OrdinalIgnoreCase))
                {
                    // Row-owned readiness (openai-compatible): no global section.
                    // The only possible blocker is a missing/invalid row baseUrl.
                    if (!OpenAiCompatibleRuntimeConfigurationParser.TryParseBaseUrl(
                        catalogRow.RuntimeConfigJson, out _))
                    {
                        blockers.Add(
                            $"{BlockerKeys.ProviderMissing}: openai-compatible:baseUrl is not configured on model '{modelId}'.");
                    }
                }
                else
                {
                    blockers.Add(
                        $"{BlockerKeys.ProviderMissing}: provider '{provider}' for model '{modelId}' is not a recognized chat provider.");
                }
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
                if (mode == RoutingProbeMode.NonBlocking)
                {
                    // Configuration surface: no inline network call. Runtime state from the
                    // Plane C alias mirror only — never RUNTIME_ARTIFACT_MISSING / ENDPOINT_UNREACHABLE
                    // here, so ShouldExposeChatModelOption keeps local models selectable (F-4).
                    runtimeState = ReadCachedLlamaRuntimeState();
                }
                else
                {
                    var llamaResult = await ProbeLlamaRuntimeAsync(catalogRow, cancellationToken).ConfigureAwait(false);
                    runtimeState = llamaResult.RuntimeState;
                    blockers.AddRange(llamaResult.Blockers);
                }
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
        _ = service;
        _ = mode;
        _ = modelId;
        return Array.Empty<string>();
    }

    private async Task<IReadOnlyList<string>> ProbeWarmupOrchestratorBlockersAsync(
        string service,
        ServiceModeDto mode,
        RoutingProbeMode probeMode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mode.ProviderSection)
            || !IsLocalServiceProviderSection(mode.ProviderSection))
        {
            return Array.Empty<string>();
        }

        // NonBlocking: no inline network call. Evidence comes from the Plane C cache
        // (reachability + last-known inventory), never from a live /ready GET (S-2).
        if (probeMode == RoutingProbeMode.NonBlocking)
        {
            return BuildAuxRuntimeBlockersFromCache(service);
        }

        if (_loadService is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var readiness = await _loadService.ProbeAsync(service, cancellationToken).ConfigureAwait(false);
            if (!readiness.Configured)
            {
                return Array.Empty<string>();
            }

            if (!readiness.Loaded)
            {
                var detail = string.IsNullOrWhiteSpace(readiness.Error)
                    ? "engine not loaded"
                    : readiness.Error;
                return
                [
                    $"{BlockerKeys.RuntimeState}: local engine not loaded for '{service}' ({detail})."
                ];
            }

            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Readiness probe unavailable for service '{Service}'.", service);
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Aux runtime blockers derived purely from the Plane C cache (NonBlocking). Three
    /// distinguishable outcomes replace aux's single historical runtime string (§5 P5):
    /// <list type="bullet">
    /// <item>Endpoint observed unreachable → ENDPOINT_UNREACHABLE (a shown status; config
    /// unaffected; the use-path guard still fails as today).</item>
    /// <item>Reachable but the persisted selection is absent from last-known inventory AND an
    /// install row exists → RUNTIME_ARTIFACT_MISSING (F-4 reuses the existing key; the UI can
    /// offer Remove). Requires the install-row precondition so a down host never masquerades
    /// as a deleted model.</item>
    /// <item>Otherwise → RUNTIME_STATE text sourced from cache, matching the Live wording so
    /// ShouldExposeChatModelOption and the toolbar tests keep passing.</item>
    /// </list>
    /// Unknown (never observed) yields no blocker — it is a renderable status, not an error (L-2).
    /// </summary>
    private IReadOnlyList<string> BuildAuxRuntimeBlockersFromCache(string service)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(service, ConfigurationForScope());
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            // Not configured locally: same silent behavior as the Live path's Configured=false.
            return Array.Empty<string>();
        }

        var readinessUrl = $"{adminBase.TrimEnd('/')}{(string.Equals(service, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal) ? "/health" : "/ready")}";
        if (_endpointCache is null || !_endpointCache.TryGet(readinessUrl, out var observation))
        {
            // Never observed: truthful absence, no blocker, no spinner.
            return Array.Empty<string>();
        }

        if (observation.Availability == EndpointAvailability.Unreachable)
        {
            return [$"{BlockerKeys.EndpointUnreachable}: local engine for '{service}' was not reachable (observed {observation.ObservedUtc:O})."];
        }

        // Reachable. Is the persisted selection present in the last-known inventory?
        var selectedRef = ReadPersistedSelectionSync(service);
        if (!string.IsNullOrWhiteSpace(selectedRef)
            && _inventoryMirror is not null
            && _inventoryMirror.TryGet(service, out var models, out var source, out _)
            && source != InventorySource.None
            && !models.Any(m => string.Equals(m.ModelRef, selectedRef, StringComparison.Ordinal))
            && InstallRowExists(service, selectedRef))
        {
            return [$"{BlockerKeys.RuntimeArtifactMissing}: selected local artifact '{selectedRef}' for '{service}' is not present on the reachable engine."];
        }

        // Reachable but not loaded → RUNTIME_STATE text identical in shape to the Live path.
        if (!string.Equals(observation.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            var detail = string.IsNullOrWhiteSpace(observation.RuntimeState) ? "engine not loaded" : observation.RuntimeState;
            return [$"{BlockerKeys.RuntimeState}: local engine not loaded for '{service}' ({detail})."];
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Llama runtime state for a NonBlocking probe, read from the Plane C alias mirror
    /// (key "llama", written by the probe loop). Returns null when never observed —
    /// rendered as unobserved, never a spinner (F-2/L-2). No inline network call.
    /// </summary>
    private string? ReadCachedLlamaRuntimeState()
    {
        if (_inventoryMirror is not null
            && _inventoryMirror.TryGet("llama", out var models, out var source, out _)
            && source != InventorySource.None)
        {
            // The mirror's IsActive flag encodes "loaded" for the llama alias table.
            return models.Any(m => m.IsActive) ? "loaded" : "unloaded";
        }
        return null;
    }

    private IConfiguration ConfigurationForScope()
    {
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IConfiguration>();
    }

    private static string BuildReadinessUrl(string serviceId, string adminBase)
    {
        var path = string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal) ? "/health" : "/ready";
        return $"{adminBase.TrimEnd('/')}{path}";
    }

    /// <summary>
    /// Reads the persisted ServiceModes selection synchronously via the scoped settings
    /// service. This is a DB read (Plane A), not a network call, so it is permitted on the
    /// NonBlocking path. Best-effort: any failure yields null (no artifact claim).
    /// </summary>
    private string? ReadPersistedSelectionSync(string serviceId)
    {
        // Plane A read via the injected settings service (a DB read, permitted on the
        // NonBlocking path). Resolving IApplicationSettingsService from an arbitrary scope
        // would fail under test scope factories and any host that registers it scoped-only.
        try
        {
            var localSection = Bootstrap.LocalServiceModeSelectionReader.ResolveLocalProviderSection(serviceId);
            if (localSection is null)
            {
                return null;
            }
            var modes = _settings.GetServiceModesAsync(serviceId, CancellationToken.None).GetAwaiter().GetResult();
            var selected = modes.FirstOrDefault(m => string.Equals(m.ProviderSection, localSection, StringComparison.OrdinalIgnoreCase))?.ModelId?.Trim();
            return string.IsNullOrWhiteSpace(selected) ? null : selected;
        }
        catch (Exception ex)
        {
            // Best-effort read: null (no artifact claim) is the product decision, but the
            // swallow must stay visible. It previously masked the EF ConcurrencyDetector
            // class on the NonBlocking fan-out path (rev3 plan §12.2); the read is now
            // factory-backed, so any residual failure here is a real regression.
            _logger.LogWarning(ex, "NonBlocking selection read failed for '{Service}': {ExceptionType}.", serviceId, ex.GetType().FullName);
            return null;
        }
    }

    /// <summary>
    /// True when the API owns an install row (Plane B) for this service+ref. The
    /// RUNTIME_ARTIFACT_MISSING blocker requires this precondition so a merely-down host
    /// can never masquerade as a deleted model (T-U7). Before P5 lands there are no rows,
    /// so this returns false and no artifact claim is made — a legitimate pre-migration state.
    /// </summary>
    private bool InstallRowExists(string serviceId, string modelRef)
    {
        // Plane B install memory (P5): the RUNTIME_ARTIFACT_MISSING blocker requires the
        // API to own a row for this ref, so a merely-down host can never masquerade as a
        // deleted model (T-U7). No row => "provenance unknown", never an artifact claim.
        if (_installStore is null)
        {
            return false;
        }

        try
        {
            return _installStore.TryGetAsync(serviceId, modelRef).GetAwaiter().GetResult() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Install-row lookup failed for '{Service}'/'{Ref}'.", serviceId, modelRef);
            return false;
        }
    }

    private static bool IsLocalServiceProviderSection(string providerSection) =>
        string.Equals(providerSection, $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl", StringComparison.Ordinal)
        || string.Equals(providerSection, $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl", StringComparison.Ordinal)
        || string.Equals(providerSection, $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl", StringComparison.Ordinal)
        || string.Equals(providerSection, $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl", StringComparison.Ordinal);

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

        // Multi-stack: probe the row's own stack. Rows without a row-owned
        // stack (and tests without a provider) probe the global stack.
        var rowStack = _stackClients is not null ? parsed.StackBaseUrl : null;
        var inventory = string.IsNullOrWhiteSpace(rowStack)
            ? await _inventory.GetInventoryAsync(cancellationToken).ConfigureAwait(false)
            : await _inventory.GetInventoryForStackAsync(rowStack, cancellationToken).ConfigureAwait(false);
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