using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// Plane C background probe loop (15 s cadence, or faster when a refresh is
/// signaled). Reads the configured endpoint universe from <see cref="IConfiguration"/>
/// and, per cycle:
/// <list type="number">
/// <item>Probes every endpoint's readiness/health URL <b>concurrently</b>
/// (<c>Task.WhenAll</c>, never foreach+await) with the 3 s per-endpoint budget.</item>
/// <item>For reachable aux services, refreshes the last-known inventory mirror
/// from the admin listing (voice/embeddings <c>GET /admin/models</c>; image
/// <c>GET /admin/bundles</c>). DocumentIntelligence is presence-only (no
/// <c>/admin/models</c>, no local-model routes) — asserted so by T-U2.</item>
/// <item>For reachable llama, refreshes the alias-table mirror via
/// <see cref="ILlamaRuntimeInventoryService"/> (the existing live inventory).</item>
/// <item>Skips a service while an API-owned load/unload is in flight (marker on
/// <see cref="ILocalServiceLoadService"/>) so a warming engine is not double-touched.</item>
/// <item>Records each verdict out-of-band via <see cref="ILocalModelObservationRecorder"/>
/// (Plane B stamps ObservedState only; never a ServiceModes selection).</item>
/// </list>
///
/// <para>
/// D5 is preserved: every request here is a read-only status/inventory GET. The
/// engine-side <c>/ready</c> handlers read only their state snapshot — no load
/// edge. The probe loop never loads, dispatches, or runs anything.
/// </para>
///
/// <para>
/// DI lifetimes (Rev 3 correction): this singleton BackgroundService must not
/// take scoped services (<c>IApplicationSettingsService</c>,
/// <c>ILlamaRuntimeInventoryService</c>, <c>ApplicationDbContext</c>) in its
/// constructor. It resolves them per cycle through
/// <c>IServiceScopeFactory.CreateScope()</c>, exactly as
/// <c>LocalModelStartupReconciliationService</c> does.
/// </para>
/// </summary>
public sealed class LocalEndpointProbeService : BackgroundService
{
    /// <summary>Probe cadence. A tuning knob, not a law (§9 risk note).</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(15);

    private readonly ILocalEndpointCache _cache;
    private readonly ILocalInventoryMirror _mirror;
    private readonly ILocalServiceLoadService _loadService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalEndpointProbeService> _logger;

    public LocalEndpointProbeService(
        ILocalEndpointCache cache,
        ILocalInventoryMirror mirror,
        ILocalServiceLoadService loadService,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<LocalEndpointProbeService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
        _loadService = loadService ?? throw new ArgumentNullException(nameof(loadService));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the hosted-service host a beat to settle (settings table warm, etc.).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed cycle never kills the loop; the next cycle retries.
                _logger.LogDebug(ex, "Reachability probe cycle failed; will retry.");
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(ProbeInterval);
                await Task.WhenAny(_cache.WaitForRefreshAsync(stoppingToken), Task.Delay(ProbeInterval, timeoutCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var universe = ResolveEndpointUniverse();
        if (universe.Count == 0)
        {
            return;
        }

        var perService = new List<Task>(universe.Count);
        foreach (var endpoint in universe)
        {
            // In-flight skip: an API-owned load/unload already owns this engine's
            // readiness channel; probing it during a warm load is wasteful
            // (§9 probe-load risk). The marker lives on LocalServiceLoadService.
            if (endpoint.ServiceId is { Length: > 0 }
                && _loadService.IsOperationInFlight(endpoint.ServiceId))
            {
                continue;
            }

            perService.Add(ProbeEndpointAsync(endpoint, cancellationToken));
        }

        await Task.WhenAll(perService).ConfigureAwait(false);
    }

    private async Task ProbeEndpointAsync(EndpointTarget endpoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.ReadinessUrl))
        {
            return;
        }

        var observation = await _cache.ObserveAsync(endpoint.ReadinessUrl, cancellationToken).ConfigureAwait(false);

        // Transition-only logging: detail on the FIRST transition into
        // Unreachable, then quiet. The cache owns the transition decision
        // atomically (ConsumeFirstUnreachable), so a warming/blackholed engine
        // logs once, not every 15 s cycle — the inventory-service LogWarning
        // flood this plane exists to stop (§9 / LlamaRuntimeInventoryService.cs:147).
        if (observation.Availability == EndpointAvailability.Unreachable)
        {
            if (_cache.ConsumeFirstUnreachable(endpoint.ReadinessUrl))
            {
                _logger.LogWarning(
                    "Local endpoint {UrlKey} became unreachable: {Error}",
                    endpoint.ReadinessUrl, observation.Error);
            }
            else
            {
                _logger.LogDebug("Local endpoint {UrlKey} still unreachable ({Failures} consecutive): {Error}",
                    endpoint.ReadinessUrl, observation.ConsecutiveFailures, observation.Error);
            }
        }

        if (endpoint.ServiceId is null)
        {
            return; // llama: presence only here; inventory mirror is handled below.
        }

        var reachable = observation.Availability == EndpointAvailability.Reachable;

        // Inventory + artifact-missing comparison + Plane B recording.
        using var scope = _scopeFactory.CreateScope();
        var recorder = scope.ServiceProvider.GetService<ILocalModelObservationRecorder>();
        try
        {
            bool? selectedArtifactPresent = null;
            if (reachable)
            {
                selectedArtifactPresent = await RefreshServiceInventoryAsync(scope, endpoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _mirror.MarkFailed(endpoint.ServiceId, observation.ObservedUtc ?? DateTime.UtcNow);
            }

            if (recorder is not null)
            {
                await recorder.RecordServiceAsync(
                        endpoint.ServiceId,
                        reachable,
                        selectedArtifactPresent,
                        reachable ? null : observation.Error,
                        observation.ObservedUtc ?? DateTime.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The observation sink is best-effort; a recorder failure must not
            // poison the reachability state (P6: readers stay non-throwing).
            _logger.LogDebug(ex, "Observation recording failed for {ServiceId}.", endpoint.ServiceId);
        }

        // Llama alias table: the existing live inventory, kept in the mirror so
        // configuration surfaces render it without an inline probe (R1).
        if (string.Equals(endpoint.ServiceId, "llama", StringComparison.Ordinal) && reachable)
        {
            await RefreshLlamaAliasMirrorAsync(scope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool?> RefreshServiceInventoryAsync(IServiceScope scope, EndpointTarget endpoint, CancellationToken cancellationToken)
    {
        var serviceId = endpoint.ServiceId!;
        var settings = scope.ServiceProvider.GetService<IApplicationSettingsService>();
        string? selectedRef = settings is not null
            ? await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(settings, serviceId, cancellationToken).ConfigureAwait(false)
            : null;

        var inventoryUrl = endpoint.InventoryUrl;
        if (string.IsNullOrWhiteSpace(inventoryUrl))
        {
            // No inventory surface for this service (presence-only, e.g.
            // DocumentIntelligence). Nothing to compare.
            return null;
        }

        string? body;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(LocalEndpointProbe.ProbeTimeout);
            var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(LocalServiceLoadService.HttpClientName);
            using var response = await client.GetAsync(inventoryUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _mirror.MarkFailed(serviceId, DateTime.UtcNow);
                return null;
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Reachable for /ready but the listing failed: keep the last-known
            // snapshot, do not clobber it with emptiness (P6).
            _logger.LogDebug(
                "Inventory listing for {ServiceId} at {Url} failed; keeping last-known snapshot: {Error}",
                serviceId, inventoryUrl, ex.Message);
            _mirror.MarkFailed(serviceId, DateTime.UtcNow);
            return null;
        }

        var options = ParseInventory(body, string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal));
        _mirror.SetLive(serviceId, options, DateTime.UtcNow);

        // Artifact-missing detection, one place (§5 P5): the persisted selection
        // (Plane A) compared against the last-known inventory (Plane C). The probe
        // loop owns this comparison and records it out-of-band.
        if (string.IsNullOrWhiteSpace(selectedRef))
        {
            return null; // no persisted selection: no artifact question
        }

        return options.Any(o => string.Equals(o.ModelRef, selectedRef, StringComparison.Ordinal));
    }

    private async Task RefreshLlamaAliasMirrorAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var inventoryService = scope.ServiceProvider.GetRequiredService<ILlamaRuntimeInventoryService>();
            var items = await inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            _mirror.SetLive(
                "llama",
                items.Select(i => new NotebookToolbarLocalModelOptionDto(
                        i.RouterModelId,
                        i.RouterModelId,
                        IsComplete: i.HasModelFile,
                        IsActive: string.Equals(i.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase)))
                    .ToList(),
                now);
        }
        catch (Exception ex)
        {
            _mirror.MarkFailed("llama", DateTime.UtcNow);
            _logger.LogDebug(ex, "Llama alias mirror refresh failed; keeping last-known snapshot.");
        }
    }

    /// <summary>
    /// The configured endpoint universe: the llama base plus the four local
    /// service hosts, each gated by HasUsableUrl. MediaBaseUrl exists in config
    /// but is NOT a routed service — excluded. DocumentIntelligence is present
    /// (its own BaseUrl) but presence-only: no readiness body semantics and no
    /// inventory surface.
    /// </summary>
    private List<EndpointTarget> ResolveEndpointUniverse()
    {
        var list = new List<EndpointTarget>();
        var cfg = _configuration;

        // Local services sit behind the guideants-ai nginx admin prefixes
        // (/asr, /tts, /emb, /sd); resolve them exactly like every other
        // local-admin caller (LocalServiceAdminRouting) so the probe and the
        // load path hit the same URLs.
        void AddLocal(string serviceId, string readinessPath, string? inventoryPath)
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, cfg);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return;
            }

            list.Add(new EndpointTarget(
                serviceId,
                $"{adminBase.TrimEnd('/')}{readinessPath}",
                inventoryPath is null ? null : $"{adminBase.TrimEnd('/')}{inventoryPath}"));
        }

        // Llama stack: the configured base already carries the stack prefix
        // (e.g. http://host:8110/llama-cpp); readiness is GET {base}/ready.
        var llamaBase = cfg["LlamaCpp:BaseUrl"];
        if (RuntimeConfigurationPlaceholders.HasUsableUrl(llamaBase) && llamaBase is not null)
        {
            list.Add(new EndpointTarget("llama", $"{llamaBase.TrimEnd('/')}/ready", null));
        }

        AddLocal(RoutedServiceNames.SpeechTranscription, "/ready", "/admin/models");
        AddLocal(RoutedServiceNames.SpeechSynthesis, "/ready", "/admin/models");
        AddLocal(RoutedServiceNames.Embeddings, "/ready", "/admin/models");
        AddLocal(RoutedServiceNames.ImageGeneration, "/health", "/admin/bundles");

        // DocumentIntelligence is an upstream image (docling-serve), presence-only:
        // any HTTP response is Reachable; no readiness body semantics, no inventory.
        var doclingBase = cfg["LocalServiceHosts:DocumentIntelligenceBaseUrl"];
        if (RuntimeConfigurationPlaceholders.HasUsableUrl(doclingBase))
        {
            list.Add(new EndpointTarget(RoutedServiceNames.DocumentIntelligence, doclingBase!.TrimEnd('/'), null));
        }

        return list;
    }

    /// <summary>
    /// Parses an inventory listing into the shared toolbar option shape. Voice
    /// listings are <c>{modelDir, items:[...]}</c> (or a bare array); image
    /// bundles are <c>{items:[{bundleId,complete,loaded}]}</c>. Skip rules match
    /// the toolbar's parsers (dot-prefixed and tokenizer-only entries) so the
    /// mirror and the UI agree on what counts as a model.
    /// </summary>
    internal static IReadOnlyList<NotebookToolbarLocalModelOptionDto> ParseInventory(string body, bool isImage)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<NotebookToolbarLocalModelOptionDto>();
        }

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        var items = root.ValueKind == System.Text.Json.JsonValueKind.Array
            ? root
            : root.TryGetProperty("items", out var nested) ? nested : root;

        if (items.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return Array.Empty<NotebookToolbarLocalModelOptionDto>();
        }

        var result = new List<NotebookToolbarLocalModelOptionDto>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            static string? Str(System.Text.Json.JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            static bool Bool(System.Text.Json.JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

            if (isImage)
            {
                var id = Str(item, "bundleId") ?? Str(item, "id");
                if (string.IsNullOrWhiteSpace(id) || id!.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(new NotebookToolbarLocalModelOptionDto(
                    id,
                    id,
                    Bool(item, "complete"),
                    Bool(item, "loaded")));
                continue;
            }

            var ref_ = Str(item, "modelRef") ?? Str(item, "model_id") ?? Str(item, "modelId") ?? Str(item, "id");
            if (string.IsNullOrWhiteSpace(ref_) || ref_!.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var activeTokenizer = Bool(item, "activeTokenizer");
            var activeModel = Bool(item, "activeModel");
            if (activeTokenizer && !activeModel)
            {
                // Tokenizer-only artifacts are runtime dependencies, not selectable models.
                continue;
            }

            result.Add(new NotebookToolbarLocalModelOptionDto(
                ref_,
                ref_,
                Bool(item, "complete"),
                Bool(item, "active") || activeModel));
        }

        return result;
    }

    private sealed record EndpointTarget(string? ServiceId, string? ReadinessUrl, string? InventoryUrl);
}
