namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// Presence probe abstraction for the reachability cache. Kept as an interface
/// so the cache and the background loop can be exercised against a deterministic
/// probe in unit tests (T-U1/T-U2) without real sockets.
/// </summary>
public interface ILocalEndpointProbe
{
    Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken);
}
