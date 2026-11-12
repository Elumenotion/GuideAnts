using GuideAntsApi.Services.Reachability;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Plane C → Plane B observation bridge. The probe loop resolves this per cycle through a
/// DI scope and reports each service's reachability and whether the persisted selection is
/// present in the last-known inventory. This record stamps ObservedState/ObservedUtc/
/// ObservedError on install rows ONLY — it is observability, never an instruction, and it
/// can never write a ServiceModes selection (D4; T-A2).
/// </summary>
public sealed class LocalModelObservationRecorder : ILocalModelObservationRecorder
{
    private readonly IServiceLocalModelInstallationStore _store;
    private readonly ILogger<LocalModelObservationRecorder> _logger;

    public LocalModelObservationRecorder(IServiceLocalModelInstallationStore store, ILogger<LocalModelObservationRecorder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RecordServiceAsync(string serviceId, bool reachable, bool? selectedArtifactPresent, string? error, DateTime observedUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await _store.ListForServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                // The presence comparison the probe loop made applies to the selected ref;
                // other rows keep their prior observation. Stamp the selected row; for the
                // others record only that the service was reachable (presence null).
                var isSelection = selectedArtifactPresent is not null;
                await _store.MarkObservedAsync(serviceId, row.ModelRef, isSelection ? selectedArtifactPresent : null, error, observedUtc, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Observation recording is best-effort; must never poison reachability (P6).
            _logger.LogDebug(ex, "Failed to record observation for {ServiceId}.", serviceId);
        }
    }
}
