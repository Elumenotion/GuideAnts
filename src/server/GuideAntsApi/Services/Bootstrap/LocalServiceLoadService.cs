using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Readiness snapshot for one local AI service, probed directly from its own
/// admin endpoint. The API is the only actor with loading authority: it talks
/// to each service independently. A service's state never affects another.
/// </summary>
public sealed record LocalServiceReadiness
{
    public required string ServiceId { get; init; }

    /// <summary>True when the service's admin base URL is configured.</summary>
    public bool Configured { get; init; }

    /// <summary>True when a model/bundle is currently loaded and serving.</summary>
    public bool Loaded { get; init; }

    /// <summary>True when a load is in flight.</summary>
    public bool Loading { get; init; }

    public string? ModelRef { get; init; }

    public string? Error { get; init; }
}

public sealed record LocalServiceOperationResult
{
    public required string ServiceId { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public LocalServiceReadiness? Readiness { get; init; }

    public static LocalServiceOperationResult Failure(string serviceId, string error) =>
        new() { ServiceId = serviceId, Success = false, Error = error };
}

/// <summary>
/// Direct per-service loading authority for the local AI backends
/// (SpeechTranscription, Embeddings, SpeechSynthesis, ImageGeneration).
/// Each operation targets exactly one service's admin endpoint; there is no
/// multi-service plan, no desired-state document, and no cross-service coupling.
/// </summary>
public interface ILocalServiceLoadService
{
    IReadOnlyCollection<string> AllServiceIds { get; }

    /// <summary>
    /// True while an API-owned load/unload is in flight for the service. The
    /// Plane C probe loop uses this to skip an engine that a load operation
    /// already owns (probe-load risk, §9). Set around load/unload, cleared in
    /// finally — the marker never outlives the operation.
    /// </summary>
    bool IsOperationInFlight(string serviceId);

    Task<LocalServiceReadiness> ProbeAsync(string serviceId, CancellationToken cancellationToken = default);

    Task<LocalServiceOperationResult> LoadServiceAsync(
        string serviceId,
        string modelRef,
        CancellationToken cancellationToken = default);

    Task<LocalServiceOperationResult> UnloadServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure a service has its persisted local model loaded. Probes readiness;
    /// if not loaded and a model ref is persisted, loads it. If no ref is
    /// persisted, returns success (nothing to load). This is the "if it is not
    /// started, start it and load it" rule.
    /// </summary>
    Task<LocalServiceOperationResult> EnsureLoadedAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup path: for every service whose active provider is local with a
    /// persisted model ref, ensure it is loaded. Sequential and independent; a
    /// failure on one service logs and continues to the next.
    /// </summary>
    Task<IReadOnlyList<LocalServiceOperationResult>> StartupEnsureAsync(
        CancellationToken cancellationToken = default);
}

public sealed class LocalServiceLoadService : ILocalServiceLoadService
{
    public const string HttpClientName = "LocalServiceAdmin";

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Probe-only budget. Readiness polling is a liveness check, not a load:
    /// a 3 s bound keeps a dead or blackholed engine from stalling readiness
    /// (the incident that motivated Plane C). Load/verify keep the full
    /// <see cref="ReadyTimeout"/> budget (R3).
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly ConcurrentDictionary<string, byte> _inFlightOperations = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalServiceLoadService> _logger;

    public LocalServiceLoadService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<LocalServiceLoadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }


    public IReadOnlyCollection<string> AllServiceIds { get; } = new[]
    {
        RoutedServiceNames.SpeechTranscription,
        RoutedServiceNames.Embeddings,
        RoutedServiceNames.SpeechSynthesis,
        RoutedServiceNames.ImageGeneration,
    };

    public bool IsOperationInFlight(string serviceId) =>
        serviceId is not null && _inFlightOperations.ContainsKey(serviceId);

    public async Task<LocalServiceReadiness> ProbeAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        var adminBase = ResolveAdminBase(serviceId);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return new LocalServiceReadiness
            {
                ServiceId = serviceId,
                Configured = false,
                Error = "No local admin base URL is configured for this service.",
            };
        }

        try
        {
            // Probe-only 3 s budget (linked): a blackholed engine must not be
            // able to hold a readiness check for 300 s. Load/unload keep
            // HttpTimeout on their own requests.
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(ProbeTimeout);
            using var response = await CreateClient(adminBase)
                .GetAsync(IsImageGeneration(serviceId) ? "health" : "ready", HttpCompletionOption.ResponseContentRead, probeCts.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(probeCts.Token).ConfigureAwait(false);
            return ParseReadiness(serviceId, adminBase, (int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LocalServiceReadiness
            {
                ServiceId = serviceId,
                Configured = true,
                Error = $"Readiness probe failed: {ex.Message}",
            };
        }
    }

    public async Task<LocalServiceOperationResult> LoadServiceAsync(
        string serviceId,
        string modelRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return LocalServiceOperationResult.Failure(serviceId, "A model reference is required to load.");
        }

        var adminBase = ResolveAdminBase(serviceId);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return LocalServiceOperationResult.Failure(serviceId, "No local admin base URL is configured for this service.");
        }

        var payload = IsImageGeneration(serviceId)
            ? $$"""{"bundle_id":"{{modelRef}}"}"""
            : $$"""{"model_path":"{{modelRef}}"}""";

        // In-flight marker: the Plane C probe loop skips this engine while a
        // load owns it; cleared in the outer finally so it never outlives the op.
        _inFlightOperations[serviceId] = 1;
        try
        {
            try
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await CreateClient(adminBase).PostAsync("admin/load", content, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // F-4: an unknown-model load must map to RUNTIME_ARTIFACT_MISSING with
                    // remediation, and must NOT burn the 3-minute ReadyTimeout waiting for
                    // something that cannot load. The check is deterministic, not an error
                    // string match: the live listing settles whether the ref exists (the
                    // same listing the UI shows). Anything else keeps the upstream message.
                    if (await IsUnknownModelRefAsync(serviceId, adminBase, modelRef, cancellationToken).ConfigureAwait(false))
                    {
                        return LocalServiceOperationResult.Failure(
                            serviceId,
                            $"{RoutingReadinessService.BlockerKeys.RuntimeArtifactMissing}: '{modelRef}' is not present on the local {serviceId} engine. Download it first, then load.");
                    }

                    return LocalServiceOperationResult.Failure(
                        serviceId,
                        $"Load failed ({(int)response.StatusCode}): {Truncate(body, 400)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return LocalServiceOperationResult.Failure(serviceId, $"Load request failed: {ex.Message}");
            }

            var readiness = await WaitForLoadedAsync(serviceId, cancellationToken).ConfigureAwait(false);
            return readiness.Loaded
                ? new LocalServiceOperationResult { ServiceId = serviceId, Success = true, Readiness = readiness }
                : LocalServiceOperationResult.Failure(serviceId, "Load command accepted but the service did not report loaded in time.");
        }
        finally
        {
            _inFlightOperations.TryRemove(serviceId, out _);
        }
    }

    public async Task<LocalServiceOperationResult> UnloadServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        var adminBase = ResolveAdminBase(serviceId);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return LocalServiceOperationResult.Failure(serviceId, "No local admin base URL is configured for this service.");
        }

        // In-flight marker (cleared in the outer finally).
        _inFlightOperations[serviceId] = 1;
        try
        {
            try
            {
                using var response = await CreateClient(adminBase).PostAsync("admin/unload", null, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return LocalServiceOperationResult.Failure(
                        serviceId,
                        $"Unload failed ({(int)response.StatusCode}): {Truncate(body, 400)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return LocalServiceOperationResult.Failure(serviceId, $"Unload request failed: {ex.Message}");
            }

            var readiness = await ProbeAsync(serviceId, cancellationToken).ConfigureAwait(false);
            return new LocalServiceOperationResult { ServiceId = serviceId, Success = true, Readiness = readiness };
        }
        finally
        {
            _inFlightOperations.TryRemove(serviceId, out _);
        }
    }

    public async Task<LocalServiceOperationResult> EnsureLoadedAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        var readiness = await ProbeAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (!readiness.Configured)
        {
            return LocalServiceOperationResult.Failure(serviceId, readiness.Error ?? "Service is not configured.");
        }

        if (readiness.Loaded)
        {
            return new LocalServiceOperationResult { ServiceId = serviceId, Success = true, Readiness = readiness };
        }

        // IApplicationSettingsService is scoped; this service is a singleton. Resolve it
        // through a scope kept alive for the duration of the operation.
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        var modelRef = await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(settings, serviceId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            // Nothing persisted to load. The service is correctly idle.
            return new LocalServiceOperationResult
            {
                ServiceId = serviceId,
                Success = true,
                Readiness = readiness,
                Error = "No local model is selected in ServiceModes; nothing to load.",
            };
        }

        return await LoadServiceAsync(serviceId, modelRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocalServiceOperationResult>> StartupEnsureAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        var results = new List<LocalServiceOperationResult>(AllServiceIds.Count);
        foreach (var serviceId in AllServiceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var isLocal = await LocalServiceModeSelectionReader.HasLocalServiceModeAsync(settings, serviceId, cancellationToken).ConfigureAwait(false);
                if (!isLocal)
                {
                    results.Add(new LocalServiceOperationResult
                    {
                        ServiceId = serviceId,
                        Success = true,
                        Error = "Active provider is not local; skipped.",
                    });
                    continue;
                }

                results.Add(await EnsureLoadedAsync(serviceId, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup ensure failed for local service {ServiceId}; continuing with remaining services.", serviceId);
                results.Add(LocalServiceOperationResult.Failure(serviceId, ex.Message));
            }
        }

        return results;
    }

    private async Task<LocalServiceReadiness> WaitForLoadedAsync(string serviceId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        var last = await ProbeAsync(serviceId, cancellationToken).ConfigureAwait(false);
        while (!last.Loaded)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return last;
            }

            try
            {
                await Task.Delay(ReadyPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return last;
            }

            last = await ProbeAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }

        return last;
    }

    private string? ResolveAdminBase(string serviceId) =>
        LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);

    private static bool IsImageGeneration(string serviceId) =>
        string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal);

    private HttpClient CreateClient(string adminBase)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = HttpTimeout;
        // BaseAddress must end with '/' so that relative request URIs
        // ("ready", "health", "admin/load", "admin/unload") resolve under the
        // per-service admin prefix (e.g. /asr/ready) instead of collapsing to the
        // server root (/ready), which nginx answers with a 404 HTML page.
        client.BaseAddress = new Uri(adminBase.EndsWith('/') ? adminBase : adminBase + "/");
        return client;
    }

    /// <summary>
    /// True when the requested modelRef is absent from the engine's live model listing.
    /// Deterministic unknown-model detection (F-4): the same listing the UI displays, not an
    /// error-string pattern. Any listing failure yields false — the upstream error text
    /// stands and the load keeps its normal failure semantics.
    /// </summary>
    private async Task<bool> IsUnknownModelRefAsync(string serviceId, string adminBase, string modelRef, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            var listPath = IsImageGeneration(serviceId) ? "admin/bundles" : "admin/models";
            using var response = await CreateClient(adminBase).GetAsync(listPath, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("items", out var nested) ? nested : default;

            if (items.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in items.EnumerateArray())
            {
                static string? Str(System.Text.Json.JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                var ref_ = Str(item, "modelRef") ?? Str(item, "bundleId") ?? Str(item, "model_id") ?? Str(item, "id");
                if (string.Equals(ref_, modelRef, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false; // listing unavailable: not provably unknown
        }
    }

    private static LocalServiceReadiness ParseReadiness(string serviceId, string adminBase, int statusCode, string body)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = document.RootElement;

        bool GetBool(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        string? GetString(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ASR/TTS/EMB: /ready -> {"ready":bool,"loaded":bool,"loading":bool,"modelRef":...}.
        // "loaded" is the model-in-memory + engine-alive signal. "ready" additionally
        // gates on the representative warmup having succeeded, so a freshly loaded
        // model reports loaded=true/ready=false until warmup completes. Load completion
        // is tracked off "loaded", not "ready", so the API does not block on warmup.
        var loaded = GetBool("loaded");
        var loading = GetBool("loading");
        var modelRef = GetString("modelRef") ?? GetString("model_path") ?? GetString("bundleId") ?? GetString("loadedBundleId");
        var error = GetString("loadError") ?? GetString("error") ?? GetString("message");

        // SD: /health -> {"status":"unloaded"|"ok"|"degraded","loadedBundleId":...}
        var status = GetString("status");
        var sdLoaded = string.Equals(status, "ok", StringComparison.Ordinal);

        return new LocalServiceReadiness
        {
            ServiceId = serviceId,
            Configured = true,
            Loaded = loaded || sdLoaded,
            Loading = loading,
            ModelRef = modelRef,
            Error = (loaded || sdLoaded) ? null : (error ?? (statusCode >= 500 ? $"Service reported not ready ({statusCode})." : null)),
        };
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");
}
