using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;

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
    private readonly ILogger<EmbeddingsReadinessGate> _logger;

    // Latch: when 1 a load is in flight; other callers await the same result.
    private readonly SemaphoreSlim _loadLatch = new(1, 1);

    public EmbeddingsReadinessGate(
        ILocalServiceLoadService loadService,
        IServiceModeResolver serviceModeResolver,
        ILogger<EmbeddingsReadinessGate> logger)
    {
        _loadService = loadService ?? throw new ArgumentNullException(nameof(loadService));
        _serviceModeResolver = serviceModeResolver ?? throw new ArgumentNullException(nameof(serviceModeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
