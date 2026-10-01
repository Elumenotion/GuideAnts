using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using System.Net;
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
            using var response = await CreateClient(adminBase)
                .GetAsync(IsImageGeneration(serviceId) ? "health" : "ready", HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

        try
        {
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await CreateClient(adminBase).PostAsync("admin/load", content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
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

    public async Task<LocalServiceOperationResult> UnloadServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        var adminBase = ResolveAdminBase(serviceId);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return LocalServiceOperationResult.Failure(serviceId, "No local admin base URL is configured for this service.");
        }

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
        client.BaseAddress = new Uri(adminBase);
        return client;
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
