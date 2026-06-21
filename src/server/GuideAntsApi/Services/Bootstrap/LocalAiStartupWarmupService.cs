using System.Net;
using System.Text;
using System.Text.Json;
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
}

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
            _logger.LogDebug("Skipping default llama preload: LlamaCpp:BaseUrl is not configured.");
            return;
        }

        var alias = await ResolveConfiguredDefaultRouterAliasAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(alias))
        {
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

    private async Task EnsureLocalServiceLoadedAndReadyAsync(string serviceId, CancellationToken cancellationToken)
    {
        if (!await ShouldWarmLocalServiceAsync(serviceId, cancellationToken).ConfigureAwait(false))
        {
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
            var loaded = await TriggerLocalServiceLoadAsync(serviceId, adminBase, cancellationToken).ConfigureAwait(false);
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
        if (!await ShouldWarmLocalServiceAsync(serviceId, cancellationToken).ConfigureAwait(false))
        {
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
                JsonObject? body = null;
                if (!string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
                {
                    var activeRef = await TryResolveActiveModelRefAsync(client, serviceId, adminBase, cancellationToken)
                        .ConfigureAwait(false);
                    body = new JsonObject();
                    if (!string.IsNullOrWhiteSpace(activeRef))
                    {
                        body["model_path"] = activeRef;
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
                if (string.IsNullOrWhiteSpace(modelRef))
                {
                    continue;
                }

                if (IsActiveModelRef(itemNode, serviceId))
                {
                    return modelRef;
                }
            }

            // Fallback: first directory-like entry.
            foreach (var itemNode in items.OfType<JsonObject>())
            {
                var isDirectory = itemNode["isDirectory"]?.GetValue<bool?>() ?? false;
                var modelRef = itemNode["modelRef"]?.GetValue<string>();
                if (isDirectory && !string.IsNullOrWhiteSpace(modelRef))
                {
                    return modelRef;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed resolving active modelRef for service '{ServiceId}'.", serviceId);
        }

        return null;
    }

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

    private async Task<bool> ShouldWarmLocalServiceAsync(string serviceId, CancellationToken cancellationToken)
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
            return true;
        }

        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(serviceId, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(mode.ProviderSection, expectedLocalProviderSection, StringComparison.Ordinal))
            {
                return true;
            }

            _logger.LogInformation(
                "Skipping local {ServiceId} warmup: default mode '{ModeId}' routes to provider section '{ProviderSection}', not local '{LocalProviderSection}'.",
                serviceId,
                mode.ModeId,
                mode.ProviderSection,
                expectedLocalProviderSection);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve routing mode for {ServiceId}; continuing with local warmup as fallback behavior.",
                serviceId);
            return true;
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
