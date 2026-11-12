using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.NotebookHeaderToolbar;

public sealed class NotebookHeaderToolbarService : INotebookHeaderToolbarService
{
    private readonly ApplicationDbContext _db;
    private readonly IApplicationSettingsService _settings;
    private readonly IRoutingReadinessService _readiness;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly IConversationManager _conversations;
    private readonly INotebookModelRuntimeService _llamaRuntime;
    private readonly IChatDefaultsStore _chatDefaultsStore;
    private readonly IConfiguration _configuration;
    // Plane C (reachability + last-known inventory). Configuration reads here are
    // synchronous cache reads — no inline local-admin HTTP (T-A1 removes the client factory).
    private readonly ILocalEndpointCache _endpointCache;
    private readonly ILocalInventoryMirror _inventoryMirror;
    private readonly ILogger<NotebookHeaderToolbarService> _logger;

    public NotebookHeaderToolbarService(
        ApplicationDbContext db,
        IApplicationSettingsService settings,
        IRoutingReadinessService readiness,
        IChatModelResolver chatModelResolver,
        IConversationManager conversations,
        INotebookModelRuntimeService llamaRuntime,
        IChatDefaultsStore chatDefaultsStore,
        IConfiguration configuration,
        ILocalEndpointCache endpointCache,
        ILocalInventoryMirror inventoryMirror,
        ILogger<NotebookHeaderToolbarService> logger)
    {
        _db = db;
        _settings = settings;
        _readiness = readiness;
        _chatModelResolver = chatModelResolver;
        _conversations = conversations;
        _llamaRuntime = llamaRuntime;
        _chatDefaultsStore = chatDefaultsStore;
        _configuration = configuration;
        _endpointCache = endpointCache;
        _inventoryMirror = inventoryMirror;
        _logger = logger;
    }

    private sealed record LocalRuntimeProbe(
        bool Loaded,
        bool Loading,
        bool PostLoadWarming,
        bool StartupWarmupRunning)
    {
        public bool IsOperational =>
            Loaded && !Loading && !PostLoadWarming && !StartupWarmupRunning;
    }

    private sealed record LocalModelInventoryLoadResult(
        IReadOnlyList<NotebookToolbarLocalModelOptionDto> Models,
        JsonNode? Root);

    public async Task<NotebookHeaderToolbarDto> GetToolbarAsync(
        Guid notebookId,
        Guid? conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Notebooks.AnyAsync(n => n.Id == notebookId, cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Notebook not found");
        }

        var generated = DateTime.UtcNow;
        var models = await _settings.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        var orderedModels = models
            .OrderBy(m => m.DisplayOrder ?? int.MaxValue)
            .ThenBy(m => m.ModelId, StringComparer.Ordinal)
            .ToList();
        var allModelOptions = orderedModels
            .Select(m => new NotebookToolbarModelOptionDto(
                m.ModelId,
                m.DisplayName,
                m.Provider,
                m.IsActive))
            .ToList();
        var modelOptions = await BuildSelectableChatModelOptionsAsync(orderedModels, cancellationToken)
            .ConfigureAwait(false);

        // Configuration surface (R-A): NonBlocking. No live llama probe, no inline
        // reachability wait; localRuntimeOn comes from the Plane C cache (S-1/S-2).
        var chat = await BuildChatSectionAsync(
            notebookId,
            conversationId,
            orderedModels,
            allModelOptions,
            modelOptions,
            RoutingProbeMode.NonBlocking,
            cancellationToken).ConfigureAwait(false);

        var image = await BuildRoutedServiceAsync(
            RoutedServiceNames.ImageGeneration,
            "Image Generation",
            "image",
            ServiceProviderIds.ImageGenerationLocalSdHttp,
            "/admin/bundles",
            isImage: true,
            cancellationToken).ConfigureAwait(false);

        var tts = await BuildRoutedServiceAsync(
            RoutedServiceNames.SpeechSynthesis,
            "Speech Synthesis",
            "tts",
            ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
            "/admin/models",
            isImage: false,
            cancellationToken).ConfigureAwait(false);

        var asr = await BuildRoutedServiceAsync(
            RoutedServiceNames.SpeechTranscription,
            "Speech Transcription",
            "asr",
            ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
            "/admin/models",
            isImage: false,
            cancellationToken).ConfigureAwait(false);

        return new NotebookHeaderToolbarDto(
            chat,
            new[] { image, tts, asr },
            generated);
    }

    public async Task<NotebookChatReadinessDto> GetChatReadinessAsync(
        Guid notebookId,
        Guid? conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Notebooks.AnyAsync(n => n.Id == notebookId, cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Notebook not found");
        }

        var models = await _settings.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        var orderedModels = models
            .OrderBy(m => m.DisplayOrder ?? int.MaxValue)
            .ThenBy(m => m.ModelId, StringComparer.Ordinal)
            .ToList();
        var allModelOptions = orderedModels
            .Select(m => new NotebookToolbarModelOptionDto(
                m.ModelId,
                m.DisplayName,
                m.Provider,
                m.IsActive))
            .ToList();

        // Use-path surface (R3): Live, byte-for-byte as today — this drives the load
        // affordance and NoChatModelDialog. Output unchanged bit-for-bit.
        var chat = await BuildChatSectionAsync(
            notebookId,
            conversationId,
            orderedModels,
            allModelOptions,
            modelOptions: Array.Empty<NotebookToolbarModelOptionDto>(),
            RoutingProbeMode.Live,
            cancellationToken).ConfigureAwait(false);

        return new NotebookChatReadinessDto(
            chat.EffectiveModelId,
            chat.EffectiveModelDisplayName,
            chat.EffectiveProvider,
            chat.Blockers,
            chat.SupportsLocalRuntimePower,
            chat.LocalRuntimeOn,
            chat.InProgressOperationId,
            chat.InProgressState);
    }

    /// <summary>
    /// Builds the toolbar chat section. Split by caller via <paramref name="mode"/> (S-1):
    /// <list type="bullet">
    /// <item><see cref="RoutingProbeMode.NonBlocking"/> (GetToolbarAsync): configuration. The
    /// effective-model readiness probe runs NonBlocking and the live per-instance llama status
    /// (<c>_llamaRuntime.GetRuntimeStatusAsync</c>) is NOT called; localRuntimeOn is derived
    /// from the Plane C alias mirror. Never awaits reachability.</item>
    /// <item><see cref="RoutingProbeMode.Live"/> (GetChatReadinessAsync): the historical body
    /// verbatim — live ProbeChatTargetAsync, live GetRuntimeStatusAsync, the RuntimeState
    /// override, IsOnlyRuntimeStateUnloadedBlocker, requiresLoad. Byte-for-byte output.</item>
    /// </list>
    /// </summary>
    private async Task<NotebookToolbarChatDto> BuildChatSectionAsync(
        Guid notebookId,
        Guid? conversationId,
        IReadOnlyList<SettingsModelDto> orderedModels,
        IReadOnlyList<NotebookToolbarModelOptionDto> allModelOptions,
        IReadOnlyList<NotebookToolbarModelOptionDto> modelOptions,
        RoutingProbeMode mode,
        CancellationToken cancellationToken)
    {
        string? effectiveModelId = null;
        string? effectiveModelDisplay = null;
        string? selectedAssistant = null;
        IReadOnlyList<string> chatBlockers = Array.Empty<string>();
        string chatStatus = "blocked";
        string chatSummary = "Chat model unavailable";
        ChatTargetReadinessDto? chatReadiness = null;
        ChatModelReferenceKind chatReferenceKind = ChatModelReferenceKind.Direct;
        var overrideAllChatModels = _chatDefaultsStore.Current.OverrideAllChatModels;
        Guid? assistantId = null;
        string? selectedAssistantModelId = null;
        if (conversationId is { } convId)
        {
            try
            {
                var state = await _conversations.GetCurrentStateAsync(convId).ConfigureAwait(false);
                selectedAssistant = state.CurrentAssistantName;
                if (!string.IsNullOrEmpty(selectedAssistant))
                {
                    var assistantRow = await _db.Assistants
                        .AsNoTracking()
                        .Where(a => a.Name == selectedAssistant)
                        .OrderBy(a => a.IsGlobal)
                        .Select(a => new { AssistantId = (Guid?)a.Id, a.ModelId })
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);
                    assistantId = assistantRow?.AssistantId;
                    selectedAssistantModelId = assistantRow?.ModelId;
                }
            }
            catch (KeyNotFoundException)
            {
                chatBlockers = new[] { "Conversation not found." };
            }
        }

        try
        {
            var resolved = _chatModelResolver.Resolve(selectedAssistantModelId);
            effectiveModelId = resolved.ModelId;
            chatReferenceKind = resolved.ReferenceKind;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve effective chat model for toolbar");
            chatBlockers = new[] { ex.Message };
        }

        if (!string.IsNullOrWhiteSpace(effectiveModelId))
        {
            var row = allModelOptions.FirstOrDefault(m => string.Equals(m.ModelId, effectiveModelId, StringComparison.Ordinal));
            effectiveModelDisplay = row?.DisplayName ?? effectiveModelId;

            var referenceKind = chatReferenceKind switch
            {
                ChatModelReferenceKind.DefaultedTo => "defaultedTo",
                ChatModelReferenceKind.OverriddenToDefault => "overriddenToDefault",
                _ => "direct"
            };
            var chatReady = await _readiness
                .ProbeChatTargetAsync(effectiveModelId, cancellationToken, referenceKind, mode)
                .ConfigureAwait(false);
            chatReadiness = chatReady;
            chatStatus = MapChatToolbarStatus(chatReady);
            chatSummary = BuildChatSummary(effectiveModelDisplay, chatReady, conversationId == null);
            if (chatReady.Blockers.Count > 0)
            {
                chatBlockers = chatReady.Blockers;
            }
        }

        // Live per-instance llama status is a use-path probe. Under NonBlocking we do NOT
        // call it (S-2): localRuntimeOn/inProgress come from the Plane C alias mirror instead.
        NotebookLlamaRuntimeStatusDto? llamaStatus = null;
        if (mode == RoutingProbeMode.Live)
        {
            llamaStatus = await _llamaRuntime
                .GetRuntimeStatusAsync(notebookId, assistantId, cancellationToken)
                .ConfigureAwait(false);
        }
        var effectiveProvider = effectiveModelId != null
            ? orderedModels.FirstOrDefault(m => string.Equals(m.ModelId, effectiveModelId, StringComparison.Ordinal))?.Provider
            : null;
        var supportsLocalLlama = modelOptions.Any(m =>
            m.IsActive
            && string.Equals(m.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ModelId, effectiveModelId, StringComparison.Ordinal))
            || string.Equals(effectiveProvider, "llama-cpp", StringComparison.OrdinalIgnoreCase);
        bool localRuntimeOn = false;
        string? chatInProgressId;
        string? chatInProgressState;
        if (mode == RoutingProbeMode.NonBlocking)
        {
            // Configuration path: derive everything from Plane C. No live llama probe, no
            // live operation store read (S-2). Unknown => not on, not blocked, never a spinner.
            localRuntimeOn = supportsLocalLlama
                && string.Equals(chatReadiness?.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);

            if (supportsLocalLlama)
            {
                if (localRuntimeOn
                    && !string.Equals(chatStatus, "blocked", StringComparison.OrdinalIgnoreCase))
                {
                    chatSummary = BuildLocalLlamaLoadedSummary(effectiveModelDisplay);
                }
                else if (chatReadiness is not null && IsOnlyRuntimeStateUnloadedBlocker(chatReadiness))
                {
                    // Same named conversion as the Live path: an only-unloaded blocker is a
                    // "requiresLoad" affordance, not an error. The loaded-others summary is
                    // derived from the Plane C alias mirror (cache, not a live probe).
                    chatStatus = "requiresLoad";
                    chatSummary = BuildLocalLlamaRequiresLoadSummaryFromMirror(effectiveModelDisplay, orderedModels);
                    chatBlockers = Array.Empty<string>();
                }
            }

            chatInProgressId = null;
            chatInProgressState = null;
        }
        else
        {
            var liveStatus = llamaStatus!;
            var selectedLocalModelLoaded = supportsLocalLlama
                && !string.IsNullOrWhiteSpace(effectiveModelId)
                && liveStatus.LoadedModels.Any(m => string.Equals(m.ModelId, effectiveModelId, StringComparison.Ordinal));
            localRuntimeOn = supportsLocalLlama
                && (selectedLocalModelLoaded || string.Equals(liveStatus.State, "ready", StringComparison.OrdinalIgnoreCase));
            // Runtime status from NotebookModelRuntimeService may be briefly stale due cached router snapshots.
            // When readiness has a concrete llama runtime state, trust it to avoid contradictory UI like
            // "Loaded" while chat is blocked on RUNTIME_STATE: ... 'unloaded'.
            if (supportsLocalLlama && !string.IsNullOrWhiteSpace(chatReadiness?.RuntimeState))
            {
                localRuntimeOn = string.Equals(chatReadiness!.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);
            }
            var inProg = liveStatus.ActiveOperation;
            chatInProgressId = inProg is { State: not "ready" and not "failed" } ? inProg.OperationId : null;
            chatInProgressState = inProg?.State;

            if (supportsLocalLlama)
            {
                var hasActiveOperation = liveStatus.ActiveOperation is { State: not "ready" and not "failed" };
                if (hasActiveOperation)
                {
                    chatStatus = "inProgress";
                    chatSummary = BuildLocalLlamaOperationSummary(effectiveModelDisplay, liveStatus.ActiveOperation);
                    chatBlockers = Array.Empty<string>();
                }
                else if (localRuntimeOn)
                {
                    if (!string.Equals(chatStatus, "blocked", StringComparison.OrdinalIgnoreCase))
                    {
                        chatSummary = BuildLocalLlamaLoadedSummary(effectiveModelDisplay);
                    }
                }
                else if (chatReadiness is not null && IsOnlyRuntimeStateUnloadedBlocker(chatReadiness))
                {
                    chatStatus = "requiresLoad";
                    chatSummary = BuildLocalLlamaRequiresLoadSummary(effectiveModelDisplay, liveStatus);
                    chatBlockers = Array.Empty<string>();
                }
            }
        }

        return new NotebookToolbarChatDto(
            chatStatus,
            chatSummary,
            conversationId?.ToString(),
            selectedAssistant,
            effectiveModelId,
            effectiveModelDisplay,
            effectiveModelId != null
                ? allModelOptions.FirstOrDefault(m => m.ModelId == effectiveModelId)?.Provider
                : null,
            overrideAllChatModels,
            supportsLocalLlama,
            localRuntimeOn,
            modelOptions,
            chatBlockers,
            chatInProgressId,
            chatInProgressState,
            chatReferenceKind switch
            {
                ChatModelReferenceKind.DefaultedTo => "defaultedTo",
                ChatModelReferenceKind.OverriddenToDefault => "overriddenToDefault",
                _ => "direct"
            },
            // Chat carries presence ONLY (from cache under NonBlocking; null under Live so
            // the use-path output stays byte-for-byte identical).
            mode == RoutingProbeMode.NonBlocking ? BuildChatLlamaPresence() : null);
    }

    /// <summary>
    /// "requiresLoad" summary naming which other local model(s) the mirror last reported
    /// loaded, preserving the Live path's wording ("Mistral Local is currently loaded.
    /// Load Qwen Local to switch."). When the mirror has no loaded models (or was never
    /// observed) it falls back to the plain requiresLoad summary — never a blank.
    /// </summary>
    private string BuildLocalLlamaRequiresLoadSummaryFromMirror(
        string? effectiveModelDisplay,
        IReadOnlyList<SettingsModelDto> orderedModels)
    {
        var loadedRefs = Array.Empty<string>();
        if (_inventoryMirror.TryGet("llama", out var models, out var source, out _)
            && source != InventorySource.None)
        {
            loadedRefs = models
                .Where(m => m.IsActive)
                .Select(m => m.ModelRef)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var name = LocalModelDisplayName(effectiveModelDisplay);
        if (loadedRefs.Length == 0)
        {
            return $"{name} selected. No local model is loaded. Load {name} to start.";
        }

        // Map alias ids to catalog display names where the catalog knows them.
        var names = loadedRefs
            .Select(ref_ => orderedModels.FirstOrDefault(m => string.Equals(m.ModelId, ref_, StringComparison.Ordinal))?.DisplayName)
            .Select(dn => string.IsNullOrWhiteSpace(dn) ? "local model" : dn!)
            .ToList();
        var loadedSummary = names.Count == 1
            ? $"{names[0]} is currently loaded"
            : $"{names[0]} and {names.Count - 1} others are currently loaded";
        return $"{name} selected. {loadedSummary}. Load {name} to switch.";
    }

    /// <summary>Llama presence for the chat section, read from Plane C (no inline HTTP).</summary>
    private EndpointPresenceDto? BuildChatLlamaPresence()
    {
        var llamaBase = _configuration["LlamaCpp:BaseUrl"];
        if (!Configuration.RuntimeConfigurationPlaceholders.HasUsableUrl(llamaBase))
        {
            return null;
        }
        var readinessUrl = $"{llamaBase!.TrimEnd('/')}/ready";
        var availability = "Unknown";
        DateTime? observedUtc = null;
        string? error = null;
        if (_endpointCache.TryGet(readinessUrl, out var observation))
        {
            availability = observation.Availability.ToString();
            observedUtc = observation.ObservedUtc;
            error = observation.Error;
        }
        return new EndpointPresenceDto("llama", llamaBase, availability, null, null, observedUtc, error);
    }

    private async Task<IReadOnlyList<NotebookToolbarModelOptionDto>> BuildSelectableChatModelOptionsAsync(
        IReadOnlyList<SettingsModelDto> models,
        CancellationToken cancellationToken)
    {
        var activeModels = models
            .Where(m => m.IsActive)
            .ToList();
        if (activeModels.Count == 0)
        {
            return Array.Empty<NotebookToolbarModelOptionDto>();
        }

        var readinessTasks = activeModels.Select(async model =>
        {
            try
            {
                var readiness = await _readiness
                    .ProbeChatTargetAsync(model.ModelId, cancellationToken, "direct", RoutingProbeMode.NonBlocking)
                    .ConfigureAwait(false);
                return (Model: model, Readiness: readiness, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Model: model, Readiness: (ChatTargetReadinessDto?)null, Error: ex);
            }
        });

        var results = await Task.WhenAll(readinessTasks).ConfigureAwait(false);
        var selectable = new List<NotebookToolbarModelOptionDto>(results.Length);
        foreach (var result in results)
        {
            if (result.Error != null)
            {
                _logger.LogDebug(result.Error, "Failed to probe chat toolbar option readiness for {ModelId}", result.Model.ModelId);
                continue;
            }

            if (result.Readiness == null
                || !ShouldExposeChatModelOption(result.Model, result.Readiness))
            {
                continue;
            }

            selectable.Add(new NotebookToolbarModelOptionDto(
                result.Model.ModelId,
                result.Model.DisplayName,
                result.Model.Provider,
                result.Model.IsActive));
        }

        return selectable;
    }

    private static bool ShouldExposeChatModelOption(SettingsModelDto model, ChatTargetReadinessDto readiness)
    {
        if (!string.Equals(readiness.Status, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(model.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return readiness.Blockers.Count > 0
            && readiness.Blockers.All(blocker =>
                blocker.StartsWith($"{RoutingReadinessService.BlockerKeys.RuntimeState}:", StringComparison.Ordinal)
                || blocker.StartsWith($"{RoutingReadinessService.BlockerKeys.RuntimeArtifactMissing}:", StringComparison.Ordinal));
    }

    private static string MapChatToolbarStatus(ChatTargetReadinessDto r)
    {
        if (string.Equals(r.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return "ready";
        }

        if (string.Equals(r.Status, "degraded", StringComparison.OrdinalIgnoreCase))
        {
            return "degraded";
        }

        return "blocked";
    }

    private static string BuildChatSummary(string? displayName, ChatTargetReadinessDto r, bool isNotebookLevelDefault)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? r.ModelId : displayName;
        if (r.Blockers.Count == 0)
        {
            if (isNotebookLevelDefault)
            {
                return $"Default model: {name ?? "Chat"}";
            }
            return name ?? "Chat";
        }

        return $"{name ?? "Chat"} — {r.Blockers[0]}";
    }

    private static bool IsOnlyRuntimeStateUnloadedBlocker(ChatTargetReadinessDto r)
    {
        if (r.Blockers.Count != 1)
        {
            return false;
        }

        var blocker = r.Blockers[0] ?? string.Empty;
        return blocker.StartsWith($"{RoutingReadinessService.BlockerKeys.RuntimeState}:", StringComparison.Ordinal)
            && blocker.Contains("'unloaded'", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLocalLlamaLoadedSummary(string? displayName)
    {
        return $"{LocalModelDisplayName(displayName)} selected. Local model loaded.";
    }

    private static string BuildLocalLlamaOperationSummary(
        string? displayName,
        ModelLoadOperationDto? operation)
    {
        var name = LocalModelDisplayName(displayName);
        var state = operation?.State?.Trim().ToLowerInvariant();
        var detail = state switch
        {
            "queued" => "queued",
            "unloading" => "unloading the current local model",
            "loading" => "loading the selected local model",
            "verifying" => "verifying the selected local model",
            _ => "in progress"
        };
        return $"Switching to {name}: {detail}.";
    }

    private static string BuildLocalLlamaRequiresLoadSummary(
        string? displayName,
        NotebookLlamaRuntimeStatusDto llamaStatus)
    {
        var name = LocalModelDisplayName(displayName);
        var loaded = llamaStatus.LoadedModels
            .Where(m => m.RuntimeConfig != null)
            .Select(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.ModelId : m.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (loaded.Count == 0)
        {
            return $"{name} selected. No local model is loaded. Load {name} to start.";
        }

        if (loaded.Count == 1)
        {
            return $"{name} selected. {loaded[0]} is currently loaded. Load {name} to switch.";
        }

        var loadedSummary = loaded.Count == 2
            ? $"{loaded[0]} and {loaded[1]} are currently loaded"
            : $"{loaded[0]} and {loaded.Count - 1} others are currently loaded";

        return $"{name} selected. {loadedSummary}. Load {name} to switch.";
    }

    private static string LocalModelDisplayName(string? displayName)
    {
        return string.IsNullOrWhiteSpace(displayName) ? "Local model" : displayName;
    }

    private async Task<NotebookToolbarServiceDto> BuildRoutedServiceAsync(
        string serviceId,
        string displayName,
        string kind,
        string localProviderId,
        string localListPath,
        bool isImage,
        CancellationToken cancellationToken)
    {
        ServiceEditorStateDto state;
        try
        {
            state = await _settings.GetServiceEditorStateAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load service editor state for {ServiceId}", serviceId);
            return new NotebookToolbarServiceDto(
                serviceId,
                displayName,
                kind,
                "blocked",
                "Service configuration unavailable",
                string.Empty,
                string.Empty,
                false,
                false,
                Array.Empty<NotebookToolbarProviderOptionDto>(),
                null,
                new[] { "Settings service state could not be loaded." },
                Array.Empty<NotebookToolbarLocalModelOptionDto>(),
                null,
                null);
        }

        var providerOptions = state.Providers
            .Where(p => p.HasExplicitMode)
            .Select(p => new NotebookToolbarProviderOptionDto(
                p.ProviderId,
                p.ProviderSection,
                p.ProviderKind,
                p.CanActivate,
                p.ActivationBlockers,
                p.ProviderSection,
                ExtractModelId(p)))
            .ToList();

        var active = state.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, state.ActiveProviderId, StringComparison.Ordinal));
        var activeLabel = active?.ProviderSection
            ?? (string.IsNullOrWhiteSpace(state.ActiveProviderId) ? "Not configured" : state.ActiveProviderId);

        var supportsPower = string.Equals(state.ActiveProviderId, localProviderId, StringComparison.Ordinal);
        var readiness = state.Readiness;
        var status = MapServiceStatus(readiness.Status);
        var blockers = readiness.Blockers.ToList();
        if (readiness.Warnings.Count > 0)
        {
            status = "degraded";
        }

        var localProviderState = state.Providers.FirstOrDefault(provider =>
            string.Equals(provider.ProviderId, localProviderId, StringComparison.Ordinal));
        var hasLocalServiceMode = localProviderState?.HasExplicitMode ?? false;

        IReadOnlyList<NotebookToolbarLocalModelOptionDto> localModels;
        string? selectedLocalRef = null;
        if (hasLocalServiceMode)
        {
            var inventory = LoadLocalModelInventoryFromMirror(serviceId);
            localModels = inventory.Models;

            // A saved local selection is operational only while routing is local.
            // When a cloud provider is active, retain raw inventory state but do
            // not present the dormant ServiceModes value as an active model.
            if (supportsPower)
            {
                selectedLocalRef = await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(
                        _settings,
                        serviceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(selectedLocalRef))
                {
                    selectedLocalRef = ResolveSelectedLocalModelRef(state, localProviderId);
                }

                localModels = isImage
                    ? MarkImageBundleSelection(localModels, selectedLocalRef)
                    : MarkVoiceModelSelection(localModels, selectedLocalRef);
            }
        }
        else
        {
            localModels = Array.Empty<NotebookToolbarLocalModelOptionDto>();
        }

        var selection = supportsPower
            ? BuildSelectionForService(isImage, localModels, selectedLocalRef)
            : null;
        var runtimeProbe = supportsPower
            ? ProbeLocalRuntimeFromCache(serviceId)
            : new LocalRuntimeProbe(false, false, false, false);
        var localOn = supportsPower && runtimeProbe.IsOperational;
        string? inProgressOperationId = null;
        string? inProgressState = null;
        string summary;

        if (supportsPower)
        {
            var startupLoading = runtimeProbe.Loading && !runtimeProbe.Loaded;
            var operationActive = runtimeProbe.Loading
                || runtimeProbe.PostLoadWarming
                || runtimeProbe.StartupWarmupRunning
                || startupLoading;

            if (operationActive)
            {
                status = "inProgress";
                inProgressOperationId = "local-runtime";
                inProgressState = runtimeProbe.Loading || startupLoading
                    ? "loading"
                    : "warming";
                blockers = [];
                summary = BuildAuxLocalOperationSummary(displayName, selection, inProgressState);
            }
            else if (localOn)
            {
                summary = !string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)
                    ? BuildAuxLocalLoadedSummary(displayName, selection, isImage)
                    : BuildDefaultServiceSummary(activeLabel, readiness);
            }
            else if (blockers.Count == 0
                && string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                status = "requiresLoad";
                summary = BuildAuxLocalRequiresLoadSummary(displayName, selection, isImage);
                blockers = [];
            }
            else
            {
                summary = BuildDefaultServiceSummary(activeLabel, readiness);
            }
        }
        else
        {
            summary = BuildDefaultServiceSummary(activeLabel, readiness);
        }

        // Plane C presence stamp (shown, never awaited) + selection-exists so a deleted
        // artifact surfaces as a named condition instead of a phantom "(active)" marker.
        var presence = BuildPresenceForService(serviceId);
        var selectionExists = SelectionExistsInInventory(selectedLocalRef, localModels);

        return new NotebookToolbarServiceDto(
            serviceId,
            displayName,
            kind,
            status,
            summary,
            state.ActiveProviderId,
            activeLabel,
            supportsPower,
            localOn,
            providerOptions,
            selection,
            blockers,
            localModels,
            inProgressOperationId,
            inProgressState,
            selectionExists,
            presence,
            presence?.ObservedUtc);
    }

    /// <summary>
    /// Synchronous Plane C presence for one routed service (no inline HTTP). Unknown when
    /// never observed — rendered "not yet observed · Refresh", never a spinner (F-2).
    /// </summary>
    private EndpointPresenceDto? BuildPresenceForService(string serviceId)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return null;
        }

        var readinessUrl = $"{adminBase.TrimEnd('/')}{(isImageService(serviceId) ? "/health" : serviceId == RoutedServiceNames.DocumentIntelligence ? "" : "/ready")}";
        var availability = "Unknown";
        DateTime? observedUtc = null;
        string? error = null;
        if (_endpointCache.TryGet(readinessUrl, out var observation))
        {
            availability = observation.Availability.ToString();
            observedUtc = observation.ObservedUtc;
            error = observation.Error;
        }
        return new EndpointPresenceDto(serviceId, adminBase, availability, ArtifactPresent: null, SelectedRef: null, observedUtc, error);
    }

    /// <summary>
    /// True when there is no persisted selection, or the selected ref is present in the
    /// last-known inventory. A selection absent from a reachable inventory => false, so the
    /// UI offers Remove against a named condition rather than showing a phantom active row.
    /// </summary>
    private static bool SelectionExistsInInventory(string? selectedLocalRef, IReadOnlyList<NotebookToolbarLocalModelOptionDto> localModels)
    {
        if (string.IsNullOrWhiteSpace(selectedLocalRef))
        {
            return true;
        }
        return localModels.Any(m => string.Equals(m.ModelRef, selectedLocalRef, StringComparison.Ordinal));
    }

    private static string BuildDefaultServiceSummary(string activeLabel, ServiceEditorReadinessDto readiness)
    {
        if (readiness.Warnings.Count > 0)
        {
            return $"{activeLabel} — {readiness.Status}; {readiness.Warnings[0]}";
        }

        return $"{activeLabel} — {readiness.Status}";
    }

    private static string BuildAuxLocalOperationSummary(
        string displayName,
        NotebookToolbarSelectionDto? selection,
        string operationState)
    {
        var resource = AuxLocalResourceLabel(selection);
        var detail = operationState switch
        {
            "loading" => "loading the selected local model",
            "warming" => "warming up the local engine",
            _ => "in progress"
        };
        return $"{displayName}: {detail} for {resource}.";
    }

    private static string BuildAuxLocalLoadedSummary(
        string displayName,
        NotebookToolbarSelectionDto? selection,
        bool isImage)
    {
        var resource = AuxLocalResourceLabel(selection);
        var noun = isImage ? "bundle" : "model";
        return $"{displayName}: local {noun} {resource} loaded.";
    }

    private static string BuildAuxLocalRequiresLoadSummary(
        string displayName,
        NotebookToolbarSelectionDto? selection,
        bool isImage)
    {
        var resource = AuxLocalResourceLabel(selection);
        var noun = isImage ? "bundle" : "model";
        return $"{displayName}: {resource} selected. Load the local {noun} to start.";
    }

    private static string AuxLocalResourceLabel(NotebookToolbarSelectionDto? selection)
    {
        if (!string.IsNullOrWhiteSpace(selection?.ResourceId))
        {
            return selection.ResourceId;
        }

        return selection?.Summary ?? "local resource";
    }

    private static string MapServiceStatus(string readinessStatus) =>
        string.Equals(readinessStatus, "ready", StringComparison.OrdinalIgnoreCase) ? "ready" : "blocked";

    private static NotebookToolbarSelectionDto? BuildSelectionForService(
        bool isImage,
        IReadOnlyList<NotebookToolbarLocalModelOptionDto> localModels,
        string? selectedLocalRef)
    {
        if (!string.IsNullOrWhiteSpace(selectedLocalRef))
        {
            return new NotebookToolbarSelectionDto(
                selectedLocalRef,
                $"active: {selectedLocalRef}");
        }

        if (isImage)
        {
            return new NotebookToolbarSelectionDto(null, "no active bundle");
        }

        var pick = localModels.FirstOrDefault(m => m.ModelRef.Length > 0);
        if (pick == null)
        {
            return new NotebookToolbarSelectionDto(null, "no installed local models");
        }

        return new NotebookToolbarSelectionDto(pick.ModelRef, pick.ModelRef);
    }

    private static string? ResolveSelectedLocalModelRef(
        ServiceEditorStateDto state,
        string localProviderId)
    {
        var localProvider = state.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderId, localProviderId, StringComparison.Ordinal));
        return localProvider is null ? null : ExtractModelId(localProvider);
    }

    private static IReadOnlyList<NotebookToolbarLocalModelOptionDto> MarkImageBundleSelection(
        IReadOnlyList<NotebookToolbarLocalModelOptionDto> localModels,
        string? selectedLocalRef)
    {
        if (string.IsNullOrWhiteSpace(selectedLocalRef))
        {
            return localModels;
        }

        return localModels
            .Select(model => model with
            {
                IsActive = string.Equals(model.ModelRef, selectedLocalRef, StringComparison.Ordinal),
                DisplayLabel = string.Equals(model.ModelRef, selectedLocalRef, StringComparison.Ordinal)
                    ? $"{model.ModelRef} (active)"
                    : model.DisplayLabel,
            })
            .ToList();
    }

    private static IReadOnlyList<NotebookToolbarLocalModelOptionDto> MarkVoiceModelSelection(
        IReadOnlyList<NotebookToolbarLocalModelOptionDto> localModels,
        string? selectedLocalRef)
    {
        if (string.IsNullOrWhiteSpace(selectedLocalRef))
        {
            return localModels;
        }

        return localModels
            .Select(model => model with
            {
                IsActive = string.Equals(model.ModelRef, selectedLocalRef, StringComparison.Ordinal),
            })
            .ToList();
    }

    /// <summary>
    /// Reads local runtime state from the Plane C reachability cache (no inline HTTP).
    /// Unknown => not on, not blocked, status labelled unobserved (rendered "not yet
    /// observed · Refresh", never a spinner — F-2). IsOperational semantics preserved:
    /// Loaded and not Loading, PostLoadWarming, or StartupWarmupRunning.
    /// </summary>
    private LocalRuntimeProbe ProbeLocalRuntimeFromCache(string serviceId)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return new LocalRuntimeProbe(false, false, false, false);
        }

        var readinessUrl = $"{adminBase.TrimEnd('/')}{(isImageService(serviceId) ? "/health" : "/ready")}";
        if (!_endpointCache.TryGet(readinessUrl, out var observation)
            || observation.Availability == EndpointAvailability.Unknown)
        {
            // Never observed: truthful absence. Not on, not blocked.
            return new LocalRuntimeProbe(false, false, false, false);
        }

        if (observation.Availability == EndpointAvailability.Unreachable)
        {
            // Reachability is a shown status; it does not report loaded/loading here.
            return new LocalRuntimeProbe(false, false, false, false);
        }

        // Reachable: map the cached body-derived runtime state into the probe shape.
        var loaded = string.Equals(observation.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);
        var loading = string.Equals(observation.RuntimeState, "loading", StringComparison.OrdinalIgnoreCase);
        return new LocalRuntimeProbe(loaded, loading, false, false);
    }

    private static bool isImageService(string serviceId) =>
        string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal);

    /// <summary>
    /// Reads last-known inventory from the Plane C mirror (no inline HTTP). The mirror is
    /// written by the probe loop and by successful download/load operations only. A cold
    /// cache yields an empty list with a null root — rendered as an explicit empty state,
    /// never a blank select (P6). Same shape as before so callers are unchanged.
    /// </summary>
    private LocalModelInventoryLoadResult LoadLocalModelInventoryFromMirror(string serviceId)
    {
        if (_inventoryMirror.TryGet(serviceId, out var models, out var source, out _) && source != InventorySource.None)
        {
            return new LocalModelInventoryLoadResult(models, null);
        }
        return new LocalModelInventoryLoadResult(Array.Empty<NotebookToolbarLocalModelOptionDto>(), null);
    }





    private static string? ExtractModelId(ProviderEditorStateDto provider)
    {
        foreach (var entry in provider.Fields)
        {
            if ((string.Equals(entry.Key, "ModelId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, "Deployment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, "modelHint", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(entry.Value.Value))
            {
                return entry.Value.Value;
            }
        }

        return null;
    }
}
