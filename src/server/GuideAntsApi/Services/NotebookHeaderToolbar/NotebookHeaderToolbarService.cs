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
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalAiStartupWarmupService _warmupService;
    private readonly ILogger<NotebookHeaderToolbarService> _logger;

    public NotebookHeaderToolbarService(
        ApplicationDbContext db,
        IApplicationSettingsService settings,
        IRoutingReadinessService readiness,
        IChatModelResolver chatModelResolver,
        IConversationManager conversations,
        INotebookModelRuntimeService llamaRuntime,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILocalAiStartupWarmupService warmupService,
        ILogger<NotebookHeaderToolbarService> logger)
    {
        _db = db;
        _settings = settings;
        _readiness = readiness;
        _chatModelResolver = chatModelResolver;
        _conversations = conversations;
        _llamaRuntime = llamaRuntime;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _warmupService = warmupService;
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

        string? effectiveModelId = null;
        string? effectiveModelDisplay = null;
        string? selectedAssistant = null;
        IReadOnlyList<string> chatBlockers = Array.Empty<string>();
        string chatStatus = "blocked";
        string chatSummary = "Chat model unavailable";
        ChatTargetReadinessDto? chatReadiness = null;
        ChatModelReferenceKind chatReferenceKind = ChatModelReferenceKind.Direct;
        var overrideAllChatModels = _configuration.GetValue<bool>("ChatDefaults:OverrideAllChatModels");
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
                .ProbeChatTargetAsync(effectiveModelId, cancellationToken, referenceKind)
                .ConfigureAwait(false);
            chatReadiness = chatReady;
            chatStatus = MapChatToolbarStatus(chatReady);
            chatSummary = BuildChatSummary(effectiveModelDisplay, chatReady, conversationId == null);
            if (chatReady.Blockers.Count > 0)
            {
                chatBlockers = chatReady.Blockers;
            }
        }

        var llamaStatus = await _llamaRuntime
            .GetRuntimeStatusAsync(notebookId, assistantId, cancellationToken)
            .ConfigureAwait(false);
        var supportsLocalLlama = modelOptions.Any(m =>
            m.IsActive
            && string.Equals(m.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ModelId, effectiveModelId, StringComparison.Ordinal));
        var selectedLocalModelLoaded = supportsLocalLlama
            && !string.IsNullOrWhiteSpace(effectiveModelId)
            && llamaStatus.LoadedModels.Any(m => string.Equals(m.ModelId, effectiveModelId, StringComparison.Ordinal));
        var localRuntimeOn = supportsLocalLlama
            && (selectedLocalModelLoaded || string.Equals(llamaStatus.State, "ready", StringComparison.OrdinalIgnoreCase));
        // Runtime status from NotebookModelRuntimeService may be briefly stale due cached router snapshots.
        // When readiness has a concrete llama runtime state, trust it to avoid contradictory UI like
        // "Loaded" while chat is blocked on RUNTIME_STATE: ... 'unloaded'.
        if (supportsLocalLlama && !string.IsNullOrWhiteSpace(chatReadiness?.RuntimeState))
        {
            localRuntimeOn = string.Equals(chatReadiness!.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);
        }
        var inProg = llamaStatus.ActiveOperation;
        var chatInProgressId = inProg is { State: not "ready" and not "failed" } ? inProg.OperationId : null;
        var chatInProgressState = inProg?.State;

        if (supportsLocalLlama)
        {
            var hasActiveOperation = llamaStatus.ActiveOperation is { State: not "ready" and not "failed" };
            if (hasActiveOperation)
            {
                chatStatus = "inProgress";
                chatSummary = BuildLocalLlamaOperationSummary(effectiveModelDisplay, llamaStatus.ActiveOperation);
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
                chatSummary = BuildLocalLlamaRequiresLoadSummary(effectiveModelDisplay, llamaStatus);
                chatBlockers = Array.Empty<string>();
            }
        }

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

        var chat = new NotebookToolbarChatDto(
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
            chatInProgressState);

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
        var toolbar = await GetToolbarAsync(notebookId, conversationId, cancellationToken).ConfigureAwait(false);
        var chat = toolbar.Chat;
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
                    .ProbeChatTargetAsync(model.ModelId, cancellationToken, "direct")
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

        var localModels = await TryLoadLocalModelInventoryAsync(
            serviceId,
            localListPath,
            isImage,
            cancellationToken).ConfigureAwait(false);

        var selection = BuildSelectionForService(state, isImage, localModels);
        var runtimeProbe = supportsPower
            ? await ProbeLocalRuntimeAsync(serviceId, isImage, cancellationToken).ConfigureAwait(false)
            : new LocalRuntimeProbe(false, false, false, false);
        var localOn = supportsPower && runtimeProbe.IsOperational;
        string? inProgressOperationId = null;
        string? inProgressState = null;
        string summary;

        if (supportsPower)
        {
            var startupLoading = _warmupService.IsWarmupInProgress && !runtimeProbe.Loaded;
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
            inProgressState);
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
        ServiceEditorStateDto state,
        bool isImage,
        IReadOnlyList<NotebookToolbarLocalModelOptionDto> localModels)
    {
        if (isImage)
        {
            var act = localModels.FirstOrDefault(m => m.IsActive);
            return new NotebookToolbarSelectionDto(
                act?.ModelRef,
                act != null ? $"active: {act.ModelRef}" : "no active bundle");
        }

        var active = localModels.FirstOrDefault(m => m.IsActive);
        var pick = active ?? localModels.FirstOrDefault(m => m.ModelRef.Length > 0);
        if (pick == null)
        {
            return new NotebookToolbarSelectionDto(null, "no installed local models");
        }

        return new NotebookToolbarSelectionDto(pick.ModelRef, pick.ModelRef);
    }

    private async Task<LocalRuntimeProbe> ProbeLocalRuntimeAsync(
        string serviceId,
        bool isImage,
        CancellationToken cancellationToken)
    {
        if (isImage)
        {
            var json = await HttpGetLocalAdminJsonAsync(serviceId, "/health", cancellationToken)
                .ConfigureAwait(false);
            if (json is null)
            {
                return new LocalRuntimeProbe(false, false, false, false);
            }

            try
            {
                var node = JsonNode.Parse(json);
                var engine = node?["engine"] as JsonObject;
                var processAlive = engine?["processAlive"]?.GetValue<bool?>() ?? false;
                var healthy = engine?["healthy"]?.GetValue<bool?>() ?? false;
                var startupWarmupRunning = node?["startupWarmup"]?["running"]?.GetValue<bool?>() ?? false;
                var status = node?["status"]?.GetValue<string>();
                var loaded = processAlive && healthy
                    || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
                var loading = startupWarmupRunning || (processAlive && !healthy);
                return new LocalRuntimeProbe(loaded, loading, false, startupWarmupRunning);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Image runtime probe parse");
                return new LocalRuntimeProbe(false, false, false, false);
            }
        }

        var readyJson = await HttpGetLocalAdminJsonAsync(
            serviceId,
            "/ready",
            cancellationToken,
            readBodyOnAnyStatus: true).ConfigureAwait(false);
        if (readyJson is null)
        {
            return new LocalRuntimeProbe(false, false, false, false);
        }

        try
        {
            var node = JsonNode.Parse(readyJson);
            var loaded = node?["loaded"]?.GetValue<bool?>() ?? false;
            var loading = node?["loading"]?.GetValue<bool?>() ?? false;
            var warmupEnabled = node?["warmupEnabled"]?.GetValue<bool?>() ?? false;
            var warmupSucceeded = node?["warmupSucceeded"]?.GetValue<bool?>() ?? false;
            var postLoadWarming = loaded && warmupEnabled && !warmupSucceeded;
            return new LocalRuntimeProbe(loaded, loading, postLoadWarming, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Asr/tts runtime probe parse");
            return new LocalRuntimeProbe(false, false, false, false);
        }
    }

    private async Task<IReadOnlyList<NotebookToolbarLocalModelOptionDto>> TryLoadLocalModelInventoryAsync(
        string serviceId,
        string listPath,
        bool isImage,
        CancellationToken cancellationToken)
    {
        var json = await HttpGetLocalAdminJsonAsync(serviceId, listPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<NotebookToolbarLocalModelOptionDto>();
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (isImage)
            {
                return ParseImageBundles(node);
            }

            return ParseVoiceModels(node);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local inventory parse for {ServiceId}", serviceId);
            return Array.Empty<NotebookToolbarLocalModelOptionDto>();
        }
    }

    private static IReadOnlyList<NotebookToolbarLocalModelOptionDto> ParseImageBundles(JsonNode? root)
    {
        var items = root?["items"] as JsonArray;
        if (items is null) return Array.Empty<NotebookToolbarLocalModelOptionDto>();

        var list = new List<NotebookToolbarLocalModelOptionDto>();
        var activeId = root?["activeBundleId"]?.GetValue<string>();
        foreach (var it in items)
        {
            if (it?["bundleId"]?.GetValue<string>() is not { } id)
            {
                continue;
            }
            var complete = it["complete"]?.GetValue<bool>() ?? false;
            var active = string.Equals(id, activeId, StringComparison.Ordinal);
            var label = active ? $"{id} (active)" : id;
            list.Add(new NotebookToolbarLocalModelOptionDto(id, label, complete, active));
        }
        return list;
    }

    private static IReadOnlyList<NotebookToolbarLocalModelOptionDto> ParseVoiceModels(JsonNode? root)
    {
        JsonArray? arr = root as JsonArray;
        if (arr is null)
        {
            arr = root?["items"] as JsonArray;
        }
        if (arr is null) return Array.Empty<NotebookToolbarLocalModelOptionDto>();

        var list = new List<NotebookToolbarLocalModelOptionDto>();
        foreach (var it in arr)
        {
            var id = it?["model_id"]?.GetValue<string>()
                ?? it?["modelId"]?.GetValue<string>()
                ?? it?["modelRef"]?.GetValue<string>()
                ?? it?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            if (id.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var activeTokenizer = it?["activeTokenizer"]?.GetValue<bool>() is true;
            var activeModel = it?["activeModel"]?.GetValue<bool>() is true;
            if (activeTokenizer && !activeModel)
            {
                // Tokenizer-only artifacts are runtime dependencies, not selectable service models.
                continue;
            }
            var path = it?["model_path"]?.GetValue<string>();
            var label = string.IsNullOrWhiteSpace(path) ? id : $"{id} ({path})";
            var active = it?["active"]?.GetValue<bool>() is true
                || it?["activeModel"]?.GetValue<bool>() is true
                || string.Equals(id, it?["active_model_id"]?.GetValue<string>(), StringComparison.Ordinal);
            list.Add(new NotebookToolbarLocalModelOptionDto(id, label, true, active));
        }
        return list;
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

    private async Task<string?> HttpGetLocalAdminJsonAsync(
        string serviceId,
        string path,
        CancellationToken cancellationToken,
        bool readBodyOnAnyStatus = false)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient();
        var url = $"{adminBase.TrimEnd('/')}{path}";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(new Uri(url), timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && !readBodyOnAnyStatus)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local admin GET {Url}", url);
            return null;
        }
    }
}

