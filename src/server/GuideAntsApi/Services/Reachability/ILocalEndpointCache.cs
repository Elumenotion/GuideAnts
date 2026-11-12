namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// Synchronous, in-memory reachability cache (Plane C). Readers MUST call
/// <see cref="TryGet"/> only — it performs no I/O and never blocks — so a
/// configuration surface can render presence state without ever awaiting a
/// socket (R1/R2, F-2 corollary 1). All mutation happens out-of-band via
/// <see cref="ObserveAsync"/> (single-flight) or the concrete type's
/// <c>MarkObserved</c>.
/// </summary>
public interface ILocalEndpointCache
{
    /// <summary>
    /// Reads a cached observation synchronously. Returns false (and the
    /// <see cref="EndpointObservation.Unknown"/> sentinel) before the first
    /// observation of a URL. Performs no I/O.
    /// </summary>
    bool TryGet(string baseUrl, out EndpointObservation observation);

    /// <summary>
    /// Probes <paramref name="baseUrl"/> and stores the result. Single-flight:
    /// N concurrent callers produce exactly one probe. Never awaited by
    /// configuration readers — invoked by the background probe loop and by
    /// <c>POST /api/settings/reachability/refresh</c> as fire-and-forget.
    /// </summary>
    Task<EndpointObservation> ObserveAsync(string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals interest in a re-observation. The background probe loop picks it
    /// up within one interval; the caller must NOT await the probe (F-2
    /// corollary 3 — Refresh must not become the sneaky await).
    /// </summary>
    void SignalRefresh(string baseUrl);

    /// <summary>
    /// Completes immediately when a refresh was signaled since the last cycle;
    /// otherwise when the next signal arrives or the token fires. The probe
    /// loop pairs this with its own cadence timeout.
    /// </summary>
    Task WaitForRefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True exactly once when the most recent observation of this URL was the
    /// first transition into Unreachable. Lets the probe loop log detail per
    /// transition rather than per cycle.
    /// </summary>
    bool ConsumeFirstUnreachable(string baseUrl);

    /// <summary>
    /// Direct observation write for tests and for the P5 download/load side
    /// effects (MarkObservedAsync funnels here). Bypasses the probe.
    /// </summary>
    void MarkObserved(string baseUrl, EndpointObservation observation);
}
