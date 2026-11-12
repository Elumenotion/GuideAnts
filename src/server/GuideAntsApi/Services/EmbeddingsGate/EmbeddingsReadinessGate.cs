using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.Reachability;

namespace GuideAntsApi.Services.EmbeddingsGate;

///<summary>
/// API-host implementation of the embeddings readiness gate. Probes the
/// local embeddings engine and loads the persisted model when not loaded.
/// A per-service latch coalesces concurrent callers so that a single
///<c>POST /admin/load</c> is issued even when several gated job types hit
/// the gate in the same poll cycle.
///</summary>
public sealed class EmbeddingsReadinessGate : IEmbeddingsReadinessGate
{
    internal const string LocalEmbeddingsProviderSection = "LocalServiceHosts:EmbeddingsBaseUrl";

    private readonly ILocalServiceLoadService _loadService;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly ILocalInventoryMirror? _inventoryMirror;
    private readonly ILocalEndpointCache? _endpointCache;
    private readonly ILogger<EmbeddingsReadinessGate> _logger;

    // Latch: when 1 a load is in flight; other callers await the same result.
    private readonly SemaphoreSlim _loadLatch = new(1, 1);

    private readonly IConfiguration _configuration;

    public EmbeddingsReadinessGate(
        ILocalServiceLoadService loadService,
        IServiceModeResolver serviceModeResolver,
        IConfiguration configuration,
        ILogger<EmbeddingsReadinessGate> logger,
        ILocalInventoryMirror? inventoryMirror = null,
        ILocalEndpointCache? endpointCache = null)
    {
        _loadService = loadService ?? throw new ArgumentNullException(nameof(loadService));
        _serviceModeResolver = serviceModeResolver ?? throw new ArgumentNullException(nameof(serviceModeResolver));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inventoryMirror = inventoryMirror;
        _endpointCache = endpointCache;
    }

    /// <summary>
    /// The embeddings admin base the gate itself probed (via ILocalServiceLoadService).
    /// The cache key must match what the probe loop observes; LocalServiceLoadService
    /// resolves it from configuration, so we mirror that resolution through the same
    /// helper the endpoints use.
    /// </summary>
    private string ResolveEmbeddingsAdminBase() =>
        Endpoints.LocalServiceAdminRouting.ResolveAdminBase(RoutedServiceNames.Embeddings, _configuration) ?? string.Empty;

    /// <summary>
    /// Reachable-but-absent: the endpoint answered /ready (any status) and the persisted
    /// selection is not in the last-known inventory. Requires a reachable cache observation
    /// and a live listing; when either is missing the gate keeps today's behavior (a use
    /// path keeps its guard, R3).
    /// </summary>

    public async Task<bool> UsesLocalEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(RoutedServiceNames.Embeddings, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            return string.Equals(
                mode.ProviderSection,
                LocalEmbeddingsProviderSection,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (RoutingException)
        {
            return false;
        }
    }

    public async Task<(bool Loaded, string? Error)> EnsureLocalEmbeddingsLoadedAsync(
        CancellationToken cancellationToken = default)
    {
        await _loadLatch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-probe inside the latch: another caller may have completed the load
            // while we were waiting.
            var readiness = await _loadService
                .ProbeAsync(RoutedServiceNames.Embeddings, cancellationToken)
                .ConfigureAwait(false);

            if (readiness.Loaded)
            {
                return (true, null);
            }

            if (!readiness.Configured)
            {
                return (false, readiness.Error ?? "Local embeddings admin URL is not configured.");
            }

            // P5: reachable-but-absent → fail the job once with a named reason instead of
            // issuing the load that can never succeed (today it re-triggers every poll
            // cycle). Unreachable-but-unknown keeps today's defer-and-retry below — a use
            // path keeps its guard (R3). A reachable-but-absent verdict releases the latch
            // immediately (the finally) rather than holding it through a doomed load.
            var cachedObservation = _endpointCache is not null
                && _endpointCache.TryGet($"{ResolveEmbeddingsAdminBase()}/ready", out var obs)
                ? obs
                : null;
            string? persistedSelection = null;
            try
            {
                persistedSelection = (await _serviceModeResolver
                    .ResolveAsync(RoutedServiceNames.Embeddings, modeId: null, cancellationToken)
                    .ConfigureAwait(false)).ModelId;
            }
            catch (RoutingException)
            {
                persistedSelection = null; // no routable mode: the guard below keeps today's path
            }
            if (_inventoryMirror is not null
                && cachedObservation is not null
                && cachedObservation.Availability == EndpointAvailability.Reachable
                && !string.IsNullOrWhiteSpace(persistedSelection)
                && _inventoryMirror.TryGet(RoutedServiceNames.Embeddings, out var models, out var mirrorSource, out _)
                && mirrorSource != InventorySource.None
                && !models.Any(m => string.Equals(m.ModelRef, persistedSelection, StringComparison.Ordinal)))
            {
                return (false,
                    $"{RoutingReadinessService.BlockerKeys.RuntimeArtifactMissing}: selected local artifact '{persistedSelection}' is not present on the embeddings engine. Download it first, then load.");
            }

            _logger.LogInformation(
                "Embeddings gate: engine not loaded; triggering load.");

            var result = await _loadService
                .EnsureLoadedAsync(RoutedServiceNames.Embeddings, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return (true, null);
            }

            return (false, result.Error ?? "EnsureLoadedAsync returned failure with no detail.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embeddings gate: unexpected error during load probe/ensure.");
            return (false, ex.Message);
        }
        finally
        {
            _loadLatch.Release();
        }
    }
}
