using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalAiStartupWarmupService
{
    /// <summary>
    /// True while <see cref="WarmupAllAsync"/> is running. Used by runtime status
    /// checks so the UI can wait for startup model loads instead of issuing a
    /// duplicate load request.
    /// </summary>
    bool IsWarmupInProgress { get; }

    /// <summary>
    /// Ensures local AI services are loaded and ready in deterministic order:
    /// unload auxiliary services, default llama-cpp chat target, then non-chat local services.
    /// </summary>
    Task WarmupAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the configured global default chat model is loaded when it maps
    /// to a llama-cpp catalog row.
    /// </summary>
    Task EnsureDefaultLlamaLoadedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures local non-chat services are loaded and ready.
    /// </summary>
    Task EnsureAuxiliaryServicesLoadedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads local non-chat services and waits until they report unloaded.
    /// </summary>
    Task UnloadAuxiliaryServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Single authority for reconciling one auxiliary (non-chat) local service to its
    /// desired state. This is the ONLY sanctioned way to load/unload an aux service in
    /// response to a user action (settings "Load", toolbar select/power, config change):
    /// callers write desired state (active provider + chosen model) and invoke this; they
    /// must NOT call the engine <c>/admin/load</c> or <c>/admin/unload</c> directly.
    ///
    /// Behaviour, driven purely by routing (which provider is active for the service):
    ///  - routing is local  → load <paramref name="requestedModelRef"/> (or the resolved
    ///    active/default model) and wait for readiness; the single-model engine supersedes
    ///    whatever was loaded before, so "the rest" is implicitly unloaded.
    ///  - routing is remote → unload (nothing local should be loaded).
    ///  - routing unknown    → leave engine state unchanged.
    ///
    /// A load requested while the service is NOT the active provider is refused
    /// (<see cref="LocalServiceReconcileOutcome.NotActiveProvider"/>): per D11 a locally
    /// routed service is warm and a non-local service must load nothing.
    /// </summary>
    Task<LocalServiceReconcileResult> ReconcileLocalServiceAsync(
        string serviceId,
        string? requestedModelRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads the local engine for <paramref name="serviceId"/> without changing
    /// which provider is active. Used by toolbar power-off while local routing stays selected.
    /// </summary>
    Task<LocalServiceReconcileResult> PowerOffLocalServiceEngineAsync(
        string serviceId,
        CancellationToken cancellationToken = default);
}

public enum LocalServiceReconcileOutcome
{
    /// <summary>Service routed local and is loaded + ready.</summary>
    Warm,

    /// <summary>Service routed remote/off and is unloaded.</summary>
    Idle,

    /// <summary>A load was requested but the service is not the active provider; nothing loaded.</summary>
    NotActiveProvider,

    /// <summary>Local admin base URL is not configured for this service.</summary>
    Unavailable,

    /// <summary>Routing could not be resolved; engine state left unchanged.</summary>
    RoutingUnknown,

    /// <summary>The load/unload was issued but the service did not reach the desired state in time.</summary>
    Timeout,

    /// <summary>The reconcile failed (load/unload request errored).</summary>
    Failed
}

public sealed record LocalServiceReconcileResult(LocalServiceReconcileOutcome Outcome, string? Detail = null);

public sealed class LocalAiStartupWarmupService : ILocalAiStartupWarmupService
{
    private int _warmupInProgress;

    public bool IsWarmupInProgress => Volatile.Read(ref _warmupInProgress) > 0;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly string[] AuxiliaryServiceLoadOrder =
    {
        "SpeechTranscription",
        "Embeddings",
        "SpeechSynthesis",
        "ImageGeneration"
    };
    private static readonly string[] AuxiliaryServiceUnloadOrder =
    {
        "ImageGeneration",
        "SpeechSynthesis",
        "Embeddings",
        "SpeechTranscription"
    };

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILlamaRuntimeCoordinator _coordinator;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly ILogger<LocalAiStartupWarmupService> _logger;

    public LocalAiStartupWarmupService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILlamaRuntimeCoordinator coordinator,
        IServiceModeResolver serviceModeResolver,
        ILogger<LocalAiStartupWarmupService> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _coordinator = coordinator;
        _serviceModeResolver = serviceModeResolver;
        _logger = logger;
    }

    public async Task WarmupAllAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _warmupInProgress, 1, 0) != 0)
        {
            _logger.LogDebug("Skipping duplicate local AI warmup; another warmup is already in progress.");
            return;
        }

        try
        {
            // Drain GPU/RAM from auxiliary services (including any container autoload)
            // before the LLM claims memory, then reload the full stack in order.
            await UnloadAuxiliaryServicesAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDefaultLlamaLoadedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureAuxiliaryServicesLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _warmupInProgress);
        }
    }

    public async Task EnsureDefaultLlamaLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            _logger.LogDebug("Skipping default llama reconcile: LlamaCpp:BaseUrl is not configured.");
            return;
        }

        var alias = await ResolveConfiguredDefaultRouterAliasAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(alias))
        {
            await UnloadAllLoadedLlamaAliasesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await EnsureLlamaAliasLoadedFirstAsync(alias, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to preload default llama alias '{Alias}'. Startup will continue.",
                alias);
        }
    }

    public async Task EnsureAuxiliaryServicesLoadedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var serviceId in AuxiliaryServiceLoadOrder)
        {
            await EnsureLocalServiceLoadedAndReadyAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UnloadAuxiliaryServicesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var serviceId in AuxiliaryServiceUnloadOrder)
        {
            await EnsureLocalServiceUnloadedAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> ResolveConfiguredDefaultRouterAliasAsync(CancellationToken cancellationToken)
    {
        var defaultModelId = (_configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            _logger.LogInformation("No ChatDefaults:DefaultModelId configured. Skipping default llama preload.");
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == defaultModelId)
            .Select(m => new { m.ModelId, m.Provider, m.RuntimeConfigJson, m.IsActive })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            _logger.LogWarning(
                "ChatDefaults:DefaultModelId '{ModelId}' was not found in catalog; skipping default llama preload.",
                defaultModelId);
            return null;
        }

        if (!row.IsActive)
        {
            _logger.LogWarning(
                "ChatDefaults:DefaultModelId '{ModelId}' is inactive; skipping default llama preload.",
                defaultModelId);
            return null;
        }

        if (!string.Equals(row.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "ChatDefaults:DefaultModelId '{ModelId}' uses provider '{Provider}', not llama-cpp; skipping local llama preload.",
                defaultModelId,
                row.Provider);
            return null;
        }

        if (string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
        {
            _logger.LogWarning(
                "Default llama model '{ModelId}' is missing RuntimeConfigJson; skipping preload.",
                defaultModelId);
            return null;
        }

        try
        {
            var parsed = LocalRuntimeConfigurationParser.ParseRequired(defaultModelId, row.RuntimeConfigJson);
            return parsed.RouterModelId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Default llama model '{ModelId}' has invalid RuntimeConfigJson; skipping preload.",
                defaultModelId);
            return null;
        }
    }

    private async Task EnsureLlamaAliasLoadedFirstAsync(string routerAlias, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var llamaClient = scope.ServiceProvider.GetRequiredService<ILlamaServerRuntimeClient>();

        var models = await SafeListLlamaModelsAsync(llamaClient, cancellationToken).ConfigureAwait(false);
        if (models is null)
        {
            _logger.LogWarning("Unable to query llama runtime inventory before default load.");
            return;
        }

        var loadedAliases = models.Data
            .Where(IsRouterModelLoaded)
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var loaded in loadedAliases.Where(id => !string.Equals(id, routerAlias, StringComparison.Ordinal)))
        {
            try
            {
                await using var unloadLock = await _coordinator
                    .AcquireAliasLockAsync(loaded, cancellationToken)
                    .ConfigureAwait(false);
                await llamaClient.UnloadModelAsync(loaded, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed unloading non-default llama alias '{Alias}' during warmup.", loaded);
            }
        }

        var targetLoaded = loadedAliases.Any(id => string.Equals(id, routerAlias, StringComparison.Ordinal));
        if (!targetLoaded)
        {
            await using var loadLock = await _coordinator
                .AcquireAliasLockAsync(routerAlias, cancellationToken)
                .ConfigureAwait(false);
            await llamaClient.LoadModelAsync(routerAlias, loadParams: null, cancellationToken).ConfigureAwait(false);
        }

        var readyTimeout = TimeSpan.FromSeconds(ReadPositiveInt("GA_LLAMA_READY_TIMEOUT_SECONDS", 900));
        var isLoaded = await WaitUntilAsync(async ct =>
        {
            var state = await SafeListLlamaModelsAsync(llamaClient, ct).ConfigureAwait(false);
            if (state is null)
            {
                return false;
            }

            return state.Data.Any(m =>
                string.Equals(m.Id, routerAlias, StringComparison.Ordinal)
                && IsRouterModelLoaded(m));
        }, readyTimeout, cancellationToken).ConfigureAwait(false);

        if (!isLoaded)
        {
            _logger.LogWarning(
                "Timed out waiting for default llama alias '{Alias}' to report loaded.",
                routerAlias);
        }
        else
        {
            _logger.LogInformation("Default llama alias '{Alias}' is loaded.", routerAlias);
        }
    }

    private async Task<LlamaModelsResponse?> SafeListLlamaModelsAsync(
        ILlamaServerRuntimeClient llamaClient,
        CancellationToken cancellationToken)
    {
        try
        {
            return await llamaClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to list llama models.");
            return null;
        }
    }

    public async Task<LocalServiceReconcileResult> PowerOffLocalServiceEngineAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Unavailable,
                $"Local admin base URL is not configured for '{serviceId}'.");
        }

        return await ReconcileIdleAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalServiceReconcileResult> ReconcileLocalServiceAsync(
        string serviceId,
        string? requestedModelRef = null,
        CancellationToken cancellationToken = default)
    {
        var routing = await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false);

        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Unavailable,
                $"Local admin base URL is not configured for '{serviceId}'.");
        }

        switch (routing)
        {
            case LocalRoutingDesiredState.Warm:
                return await ReconcileWarmAsync(serviceId, adminBase, requestedModelRef, cancellationToken)
                    .ConfigureAwait(false);

            case LocalRoutingDesiredState.Idle:
                if (!string.IsNullOrWhiteSpace(requestedModelRef))
                {
                    // A specific model was requested but this service is not the active
                    // provider. Loading it would violate the single-authority rule (only the
                    // active provider may be warm), so refuse instead of loading + immediately
                    // idling.
                    _logger.LogInformation(
                        "Refusing load for '{ServiceId}': it is not the active provider (routing is remote/off).",
                        serviceId);
                    return new LocalServiceReconcileResult(
                        LocalServiceReconcileOutcome.NotActiveProvider,
                        $"'{serviceId}' is not the active provider; nothing was loaded.");
                }

                return await ReconcileIdleAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);

            default:
                _logger.LogWarning(
                    "Skipping '{ServiceId}' reconcile: routing resolution failed; leaving engine state unchanged.",
                    serviceId);
                return new LocalServiceReconcileResult(
                    LocalServiceReconcileOutcome.RoutingUnknown,
                    $"Routing for '{serviceId}' could not be resolved.");
        }
    }

    private async Task<LocalServiceReconcileResult> ReconcileWarmAsync(
        string serviceId,
        string adminBase,
        string? requestedModelRef,
        CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await TriggerLocalServiceLoadAsync(serviceId, adminBase, requestedModelRef, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded)
            {
                return new LocalServiceReconcileResult(
                    LocalServiceReconcileOutcome.Failed,
                    $"Load request for '{serviceId}' did not succeed.");
            }

            var ready = await WaitForLocalServiceReadyAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                _logger.LogWarning(
                    "Timed out waiting for local service '{ServiceId}' readiness after load.",
                    serviceId);
                return new LocalServiceReconcileResult(
                    LocalServiceReconcileOutcome.Timeout,
                    $"'{serviceId}' did not report ready after load.");
            }

            _logger.LogInformation("Local service '{ServiceId}' is ready.", serviceId);
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Warm);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local service '{ServiceId}' load failed.", serviceId);
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Failed, ex.Message);
        }
    }

    private async Task<LocalServiceReconcileResult> ReconcileIdleAsync(
        string serviceId,
        string adminBase,
        CancellationToken cancellationToken)
    {
        try
        {
            var unloaded = await TriggerLocalServiceUnloadAsync(serviceId, adminBase, cancellationToken)
                .ConfigureAwait(false);
            if (!unloaded)
            {
                return new LocalServiceReconcileResult(
                    LocalServiceReconcileOutcome.Failed,
                    $"Unload request for '{serviceId}' did not succeed.");
            }

            var isUnloaded = await WaitForLocalServiceUnloadedAsync(serviceId, adminBase, cancellationToken)
                .ConfigureAwait(false);
            if (!isUnloaded)
            {
                return new LocalServiceReconcileResult(
                    LocalServiceReconcileOutcome.Timeout,
                    $"'{serviceId}' did not report unloaded.");
            }

            _logger.LogInformation("Local service '{ServiceId}' is unloaded.", serviceId);
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local service '{ServiceId}' unload failed.", serviceId);
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Failed, ex.Message);
        }
    }

    private async Task EnsureLocalServiceLoadedAndReadyAsync(string serviceId, CancellationToken cancellationToken)
    {
        var routing = await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (routing != LocalRoutingDesiredState.Warm)
        {
            if (routing == LocalRoutingDesiredState.Unknown)
            {
                _logger.LogWarning(
                    "Skipping {ServiceId} warmup: routing resolution failed.",
                    serviceId);
            }

            return;
        }

        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            _logger.LogDebug("Skipping {ServiceId} warmup: local admin base URL not configured.", serviceId);
            return;
        }

        try
        {
            var loaded = await TriggerLocalServiceLoadAsync(serviceId, adminBase, requestedModelRef: null, cancellationToken).ConfigureAwait(false);
            if (!loaded)
            {
                return;
            }

            var ready = await WaitForLocalServiceReadyAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                _logger.LogWarning(
                    "Timed out waiting for local service '{ServiceId}' readiness after load.",
                    serviceId);
                return;
            }

            _logger.LogInformation("Local service '{ServiceId}' is ready.", serviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Local service '{ServiceId}' warmup failed. Startup will continue.",
                serviceId);
        }
    }

    private async Task EnsureLocalServiceUnloadedAsync(string serviceId, CancellationToken cancellationToken)
    {
        var routing = await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (routing == LocalRoutingDesiredState.Warm)
        {
            // Warm services are unloaded in the reverse-order pass before llama load, then reloaded.
        }
        else if (routing == LocalRoutingDesiredState.Unknown)
        {
            _logger.LogWarning(
                "Skipping {ServiceId} unload: routing resolution failed; leaving engine state unchanged.",
                serviceId);
            return;
        }

        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            _logger.LogDebug("Skipping {ServiceId} unload: local admin base URL not configured.", serviceId);
            return;
        }

        try
        {
            var unloaded = await TriggerLocalServiceUnloadAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);
            if (!unloaded)
            {
                return;
            }

            var isUnloaded = await WaitForLocalServiceUnloadedAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);
            if (!isUnloaded)
            {
                _logger.LogWarning(
                    "Timed out waiting for local service '{ServiceId}' to report unloaded.",
                    serviceId);
                return;
            }

            _logger.LogInformation("Local service '{ServiceId}' is unloaded.", serviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Local service '{ServiceId}' unload failed. Continuing llama load flow.",
                serviceId);
        }
    }

    private async Task<bool> TriggerLocalServiceLoadAsync(
        string serviceId,
        string adminBase,
        string? requestedModelRef,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var timeout = TimeSpan.FromSeconds(GetServiceReadyTimeoutSeconds(serviceId));
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        var isImageGeneration = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal);

        // Image Generation records the desired bundle via a select-active marker; the load
        // endpoint then starts the engine against whatever bundle is marked active. When the
        // caller requested a specific bundle, set that marker first so the load resolves to it.
        if (isImageGeneration && !string.IsNullOrWhiteSpace(requestedModelRef))
        {
            await TrySelectActiveImageBundleAsync(client, adminBase, requestedModelRef, cancellationToken)
                .ConfigureAwait(false);
        }

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                JsonObject? body = null;
                if (!isImageGeneration)
                {
                    // Desired model: what the caller explicitly asked for, else the engine's
                    // resolved active/default model. Loading it on a single-model engine
                    // supersedes anything previously loaded (the "rest" is unloaded).
                    var modelRef = !string.IsNullOrWhiteSpace(requestedModelRef)
                        ? requestedModelRef
                        : await TryResolveActiveModelRefAsync(client, serviceId, adminBase, cancellationToken)
                            .ConfigureAwait(false);
                    body = new JsonObject();
                    if (!string.IsNullOrWhiteSpace(modelRef))
                    {
                        body["model_path"] = modelRef;
                    }
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}/admin/load")
                {
                    Content = body is null
                        ? null
                        : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
                {
                    return true;
                }

                // A 4xx other than 409 is a permanent, non-retryable rejection (e.g. the
                // model is not a catalog artifact, or the body is malformed). Retrying cannot
                // fix it, so fail fast instead of hammering the engine every poll until the
                // ready timeout and wedging the whole reconcile.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    _logger.LogWarning(
                        "Load request for service '{ServiceId}' was rejected ({StatusCode}) and will not be retried: {Body}",
                        serviceId,
                        (int)response.StatusCode,
                        Truncate(responseBody, 512));
                    return false;
                }

                _logger.LogDebug(
                    "Load request for service '{ServiceId}' returned {StatusCode}: {Body}",
                    serviceId,
                    (int)response.StatusCode,
                    Truncate(responseBody, 512));
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (lastError is not null)
        {
            _logger.LogWarning(lastError, "Failed issuing startup load for service '{ServiceId}'.", serviceId);
        }
        else
        {
            _logger.LogWarning("Failed issuing startup load for service '{ServiceId}' within timeout.", serviceId);
        }

        return false;
    }

    private async Task<bool> TriggerLocalServiceUnloadAsync(
        string serviceId,
        string adminBase,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var timeout = TimeSpan.FromSeconds(GetServiceReadyTimeoutSeconds(serviceId));
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}/admin/unload");
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
                {
                    return true;
                }

                _logger.LogDebug(
                    "Unload request for service '{ServiceId}' returned {StatusCode}: {Body}",
                    serviceId,
                    (int)response.StatusCode,
                    Truncate(responseBody, 512));
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (lastError is not null)
        {
            _logger.LogWarning(lastError, "Failed issuing unload for service '{ServiceId}'.", serviceId);
        }
        else
        {
            _logger.LogWarning("Failed issuing unload for service '{ServiceId}' within timeout.", serviceId);
        }

        return false;
    }

    private async Task<bool> WaitForLocalServiceReadyAsync(
        string serviceId,
        string adminBase,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var timeout = TimeSpan.FromSeconds(GetServiceReadyTimeoutSeconds(serviceId));

        if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
        {
            return await WaitUntilAsync(async ct =>
            {
                try
                {
                    using var response = await client.GetAsync($"{adminBase}/health", ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return true;
                    }

                    var root = JsonNode.Parse(body) as JsonObject;
                    var processAlive = root?["engine"]?["processAlive"]?.GetValue<bool?>();
                    var healthy = root?["engine"]?["healthy"]?.GetValue<bool?>();

                    if (processAlive.HasValue && healthy.HasValue)
                    {
                        return processAlive.Value && healthy.Value;
                    }

                    if (processAlive.HasValue)
                    {
                        return processAlive.Value;
                    }

                    var status = root?["status"]?.GetValue<string>();
                    return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }, timeout, cancellationToken).ConfigureAwait(false);
        }

        return await WaitUntilAsync(async ct =>
        {
            try
            {
                using var response = await client.GetAsync($"{adminBase}/ready", ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitForLocalServiceUnloadedAsync(
        string serviceId,
        string adminBase,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var timeout = TimeSpan.FromSeconds(GetServiceReadyTimeoutSeconds(serviceId));

        if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
        {
            return await WaitUntilAsync(async ct =>
            {
                try
                {
                    using var response = await client.GetAsync($"{adminBase}/health", ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return false;
                    }

                    var root = JsonNode.Parse(body) as JsonObject;
                    var status = root?["status"]?.GetValue<string>();
                    if (string.Equals(status, "unloaded", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    var processAlive = root?["engine"]?["processAlive"]?.GetValue<bool?>();
                    return processAlive.HasValue && !processAlive.Value;
                }
                catch
                {
                    return false;
                }
            }, timeout, cancellationToken).ConfigureAwait(false);
        }

        return await WaitUntilAsync(async ct =>
        {
            try
            {
                using var response = await client.GetAsync($"{adminBase}/ready", ct).ConfigureAwait(false);
                return !response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryResolveActiveModelRefAsync(
        HttpClient client,
        string serviceId,
        string adminBase,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"{adminBase}/admin/models", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject;
            var items = root?["items"] as JsonArray;
            if (items is null)
            {
                return null;
            }

            foreach (var itemNode in items.OfType<JsonObject>())
            {
                var modelRef = itemNode["modelRef"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(modelRef) || IsHiddenEntry(modelRef))
                {
                    continue;
                }

                if (IsActiveModelRef(itemNode, serviceId))
                {
                    return modelRef;
                }
            }

            // No model is currently active. Do NOT guess a model from the on-disk directory
            // listing: that listing can include non-catalog artifacts (e.g. a stray
            // "Kokoro-82M" folder) which the engine rejects with a permanent 4xx, and picking
            // one arbitrarily is exactly the kind of silent-wrong-default that hides bugs.
            // Returning null makes the caller send no model_path, so the engine loads its own
            // configured catalog default — the single, well-defined desired model.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed resolving active modelRef for service '{ServiceId}'.", serviceId);
        }

        return null;
    }

    private async Task TrySelectActiveImageBundleAsync(
        HttpClient client,
        string adminBase,
        string bundleId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{adminBase}/admin/bundles/{Uri.EscapeDataString(bundleId)}/select-active");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Select-active for image bundle '{BundleId}' returned {StatusCode}: {Body}",
                    bundleId,
                    (int)response.StatusCode,
                    Truncate(responseBody, 512));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to select active image bundle '{BundleId}'.", bundleId);
        }
    }

    private static bool IsHiddenEntry(string modelRef) => modelRef.StartsWith('.');

    private static bool IsActiveModelRef(JsonObject item, string serviceId)
    {
        return serviceId switch
        {
            "SpeechSynthesis" => item["activeModel"]?.GetValue<bool?>() ?? false,
            _ => item["active"]?.GetValue<bool?>() ?? false
        };
    }

    private async Task<bool> WaitUntilAsync(
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await predicate(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private int GetServiceReadyTimeoutSeconds(string serviceId)
    {
        return serviceId switch
        {
            "SpeechTranscription" => ReadPositiveInt("GA_ASR_READY_TIMEOUT_SECONDS", 900),
            "SpeechSynthesis" => ReadPositiveInt("GA_TTS_READY_TIMEOUT_SECONDS", 900),
            "Embeddings" => ReadPositiveInt("GA_EMB_READY_TIMEOUT_SECONDS", 900),
            "ImageGeneration" => ReadPositiveInt("GA_SD_READY_TIMEOUT_SECONDS", 900),
            _ => 900
        };
    }

    private int ReadPositiveInt(string key, int fallback)
    {
        var raw = _configuration[key];
        if (int.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return fallback;
    }

    private static bool IsRouterModelLoaded(LlamaModelData model)
    {
        if (!string.IsNullOrWhiteSpace(model.Status?.Value))
        {
            return string.Equals(model.Status.Value, "loaded", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(model.State))
        {
            return string.Equals(model.State, "loaded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private enum LocalRoutingDesiredState
    {
        Warm,
        Idle,
        Unknown
    }

    private async Task<LocalRoutingDesiredState> ResolveLocalRoutingDesiredStateAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var expectedLocalProviderSection = serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => "LocalServiceHosts:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => "LocalServiceHosts:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => "LocalServiceHosts:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => "LocalServiceHosts:ImageGenerationBaseUrl",
            _ => null
        };

        if (expectedLocalProviderSection is null)
        {
            return LocalRoutingDesiredState.Warm;
        }

        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(serviceId, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(mode.ProviderSection, expectedLocalProviderSection, StringComparison.Ordinal))
            {
                return LocalRoutingDesiredState.Warm;
            }

            _logger.LogInformation(
                "Local {ServiceId} should be idle: default mode '{ModeId}' routes to provider section '{ProviderSection}', not local '{LocalProviderSection}'.",
                serviceId,
                mode.ModeId,
                mode.ProviderSection,
                expectedLocalProviderSection);
            return LocalRoutingDesiredState.Idle;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve routing mode for {ServiceId}; treating as unknown (do not warm).",
                serviceId);
            return LocalRoutingDesiredState.Unknown;
        }
    }

    private async Task UnloadAllLoadedLlamaAliasesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var llamaClient = scope.ServiceProvider.GetRequiredService<ILlamaServerRuntimeClient>();

        var models = await SafeListLlamaModelsAsync(llamaClient, cancellationToken).ConfigureAwait(false);
        if (models is null)
        {
            _logger.LogWarning("Unable to query llama runtime inventory for idle unload.");
            return;
        }

        var loadedAliases = models.Data
            .Where(IsRouterModelLoaded)
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var loaded in loadedAliases)
        {
            try
            {
                await using var unloadLock = await _coordinator
                    .AcquireAliasLockAsync(loaded, cancellationToken)
                    .ConfigureAwait(false);
                await llamaClient.UnloadModelAsync(loaded, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Unloaded llama alias '{Alias}' (default chat is not llama-cpp).", loaded);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed unloading llama alias '{Alias}' during idle reconcile.", loaded);
            }
        }
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "...";
    }
}
