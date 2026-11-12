using GuideAntsApi.Services.Bootstrap;
using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// In-memory reachability cache keyed by canonical instance identity (Plane C).
///
/// <para>Keys use <see cref="LocalAiStackHostResolver.CanonicalInstanceKey"/> so
/// <c>http://max:8112/llama-cpp</c> and <c>http://192.168.0.111:8112/llama-cpp</c>
/// are one identity (same box), while distinct per-service endpoints on the same
/// host:port (the aux services and llama share a port behind nginx) stay distinct
/// by their path.</para>
///
/// <para>
/// Validity is deliberately asymmetric. Reachable entries are considered fresh
/// for <see cref="ReachableTtl"/> (60 s). Unreachable entries never fall out of
/// the cache: a down engine is a stable state, and the last known unreachable
/// observation is always renderable ("endpoint unreachable · observed HH:MM").
/// This is a deliberate inversion of the 2 s inventory failure cache
/// (<c>LlamaRuntimeInventoryService.InventoryFailureCacheTtl</c>): a failure
/// there is a blip; here it is a status. Probing cadence is owned by the
/// background loop (15 s), not by this cache.
/// </para>
/// </summary>
public sealed class LocalEndpointCache : ILocalEndpointCache
{
    /// <summary>Reachable entries are fresh for 60 s.</summary>
    public static readonly TimeSpan ReachableTtl = TimeSpan.FromSeconds(60);

    private readonly ILocalEndpointProbe _probe;
    private readonly Func<DateTime> _nowUtc;
    private readonly Func<string, string> _canonicalize;

    private readonly ConcurrentDictionary<string, EndpointObservation> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<EndpointObservation>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _refreshRequested = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _firstUnreachable = new(StringComparer.Ordinal);

    // Refresh wake coordination (F-2 corollary 3): SignalRefresh from any request
    // context wakes the background loop immediately; the loop consumes the flag
    // at the start of its next cycle so no signal is lost.
    private readonly object _gate = new();
    private bool _refreshDue;
    private TaskCompletionSource<bool>? _refreshTcs;

    public LocalEndpointCache(ILocalEndpointProbe probe, Func<DateTime>? nowUtc = null, Func<string, string>? canonicalize = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _canonicalize = canonicalize ?? (url => LocalAiStackHostResolver.CanonicalInstanceKey(url));
    }

    public bool TryGet(string baseUrl, out EndpointObservation observation)
    {
        observation = EndpointObservation.Unknown(baseUrl);
        if (!_entries.TryGetValue(Key(baseUrl), out var stored))
        {
            return false;
        }

        // Synchronous, no I/O. Stale reachable and any unreachable observation are
        // still the last known state and are returned as-is (F-2: shown, not hidden;
        // L-2: a cold/old cache is a truthful state, rendered with ObservedUtc).
        observation = stored;
        return true;
    }

    private readonly object _inFlightGate = new();

    /// <summary>
    /// Instance key: the canonical identity under the injected (or default
    /// DNS-based) canonicalizer. Every method must key through this so tests
    /// can inject a deterministic aliasing.
    /// </summary>
    private string Key(string url) => KeyFor(url, _canonicalize);

    public Task<EndpointObservation> ObserveAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        // Single-flight: N concurrent callers produce exactly one probe per key.
        // A lock guards the check-and-register (dictionary ops only — no I/O
        // under the lock). The probe runs as a normal async method OUTSIDE the
        // lock; its finally clears the slot once it settles, whether it completed
        // synchronously or asynchronously, so a key is never left shadowed.
        var key = Key(baseUrl);
        lock (_inFlightGate)
        {
            if (_inFlight.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // Task.Run defers the probe past a real await point, so the entry is
            // ALWAYS registered before the probe can complete — even when the
            // probe finishes synchronously (fast refusal / completed fake), whose
            // finally would otherwise remove the slot before the line below adds it.
            var task = Task.Run(() => ProbeOnceAsync(key, baseUrl, cancellationToken));
            _inFlight[key] = task;
            return task;
        }
    }

    private async Task<EndpointObservation> ProbeOnceAsync(string key, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var observation = await _probe.ProbeAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            var becameUnreachable = Store(key, observation);
            if (becameUnreachable)
            {
                _firstUnreachable[key] = 1;
            }
            return observation;
        }
        finally
        {
            // Runs after the probe settles (sync or async). Registered before the
            // first await, so this is the same instance callers received.
            lock (_inFlightGate)
            {
                _inFlight.Remove(key, out _);
            }
        }
    }

    /// <summary>
    /// True (once) when the most recent observation of this URL was the first
    /// transition into Unreachable. Consumed by the probe loop so the detailed
    /// warning fires exactly once per transition, not every cycle.
    /// </summary>
    public bool ConsumeFirstUnreachable(string baseUrl) =>
        _firstUnreachable.TryRemove(Key(baseUrl), out _);

    private bool Store(string key, EndpointObservation observation)
    {
        // Returns true iff this write is the FIRST transition into Unreachable
        // (prior state was Reachable or Unknown). The probe loop uses this to log
        // detail once per transition, then stay quiet — the inventory-service
        // LogWarning-per-cycle flood this plane exists to stop (§9).
        var becameUnreachable = false;
        if (observation.Availability == EndpointAvailability.Unreachable)
        {
            if (_entries.TryGetValue(key, out var existing)
                && existing.Availability == EndpointAvailability.Unreachable)
            {
                observation = observation with { ConsecutiveFailures = existing.ConsecutiveFailures + 1 };
            }
            else
            {
                becameUnreachable = true;
            }
        }

        _entries[key] = observation with { UrlKey = key };
        return becameUnreachable;
    }

    public void SignalRefresh(string baseUrl)
    {
        _refreshRequested[Key(baseUrl)] = 1;
        lock (_gate)
        {
            _refreshDue = true;
            _refreshTcs?.TrySetResult(true);
        }
    }

    /// <summary>
    /// Completes immediately when a refresh was signaled after the last cycle;
    /// otherwise completes when the next signal arrives or <paramref name="cancellationToken"/>
    /// fires. The background loop pairs this with a 15 s timeout so it also cycles
    /// on its own cadence.
    /// </summary>
    public Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_refreshDue)
            {
                _refreshDue = false;
                _refreshTcs = null;
                return Task.CompletedTask;
            }

            _refreshTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _refreshTcs.Task;
        }
    }

    /// <summary>
    /// Direct observation write for the probe loop's companion bookkeeping and
    /// for download/load side effects (P5). Bypasses the probe.
    /// </summary>
    public void MarkObserved(string baseUrl, EndpointObservation observation)
    {
        _entries[Key(baseUrl)] = observation with { UrlKey = Key(baseUrl) };
    }

    /// <summary>Full snapshot of current observations, keyed by canonical key.</summary>
    public IReadOnlyCollection<KeyValuePair<string, EndpointObservation>> Snapshot() =>
        _entries.ToList();

    /// <summary>
    /// Canonical identity: resolved host:port (IPv4-first, 80/443 elided) plus the
    /// path, so host aliases collapse while same-host service endpoints stay
    /// distinct.
    /// </summary>
    internal static string KeyFor(string url, Func<string, string>? canonicalize = null)
    {
        var canonical = canonicalize ?? (u => LocalAiStackHostResolver.CanonicalInstanceKey(u));
        var trimmed = url.Trim().TrimEnd('/');
        string net;
        string path;
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        var restStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var slash = trimmed.IndexOf('/', restStart);
        if (slash < 0)
        {
            net = trimmed;
            path = "/";
        }
        else
        {
            net = trimmed[..slash];
            path = trimmed[slash..];
        }

        return canonical(net) + path;
    }
}
