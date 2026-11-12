using FluentAssertions;
using GuideAntsApi.Services.Reachability;

namespace GuideAntsApi.Tests.Services.Reachability;

/// <summary>T-U1 — the reachability cache is synchronous, single-flight, and
/// canonicalized; Unknown is a renderable state, not a spinner.</summary>
[TestClass]
public sealed class LocalEndpointCacheTests
{
    private static LocalEndpointCache CreateCache(
        ILocalEndpointProbe probe,
        Func<DateTime>? clock = null,
        Func<string, string>? canonicalize = null) =>
        new LocalEndpointCache(probe, clock, canonicalize);

    private static EndpointObservation Reachable(string url, DateTime? at = null) =>
        new(url, EndpointAvailability.Reachable, at ?? DateTime.UtcNow, 5, null, 0);

    private static EndpointObservation Unreachable(string url, DateTime? at = null, int failures = 1) =>
        new(url, EndpointAvailability.Unreachable, at ?? DateTime.UtcNow, 5, "boom", failures);

    [TestMethod]
    public void TryGet_ReturnsUnknown_Sentinel_BeforeFirstObservation()
    {
        var probe = new ThrowingIfTouchedProbe();
        var cache = CreateCache(probe);

        var found = cache.TryGet("http://max:8112/asr/ready", out var observation);

        found.Should().BeFalse();
        observation.Availability.Should().Be(EndpointAvailability.Unknown);
        observation.ObservedUtc.Should().BeNull();
        observation.Error.Should().BeNull();
        probe.Touches.Should().Be(0, "TryGet performs no I/O");
    }

    [TestMethod]
    public void TryGet_ReturnsCachedObservation_WithoutProbing()
    {
        var probe = new ThrowingIfTouchedProbe();
        var cache = CreateCache(probe);
        var url = "http://max:8112/asr/ready";
        cache.MarkObserved(url, Reachable(url));

        var found = cache.TryGet(url, out var observation);

        found.Should().BeTrue();
        observation.Availability.Should().Be(EndpointAvailability.Reachable);
        observation.ObservedUtc.Should().NotBeNull();
        probe.Touches.Should().Be(0, "TryGet performs no I/O");
    }

    [TestMethod]
    public async Task ObserveAsync_SingleFlight_HundredConcurrentReaders_ProduceExactlyOneProbe()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new CountingProbe(() => gate.Task);
        var cache = CreateCache(probe);
        var url = "http://max:8112/tts/ready";

        var tasks = Enumerable.Range(0, 100).Select(_ => cache.ObserveAsync(url)).ToList();
        await Task.Delay(50); // let all 100 racers queue up before release
        gate.SetResult(true);
        var results = await Task.WhenAll(tasks);

        probe.Calls.Should().Be(1, "exactly one probe for 100 concurrent readers");
        results.Should().OnlyContain(r => r.Availability == EndpointAvailability.Reachable);
    }

    [TestMethod]
    public void CanonicalKey_Aliasing_ResolvedHostAndPortPlusPath_CollapsesAliases()
    {
        // Deterministic stand-in for DNS: "max" resolves to 192.168.0.111.
        var canonicalize = (Func<string, string>)(u =>
            u.Contains("max", StringComparison.Ordinal) ? u.Replace("max", "192.168.0.111", StringComparison.Ordinal) : u);
        var cache = CreateCache(new ThrowingIfTouchedProbe(), null, canonicalize);

        var a = LocalEndpointCache.KeyFor("http://max:8112/llama-cpp/ready", canonicalize);
        var b = LocalEndpointCache.KeyFor("http://192.168.0.111:8112/llama-cpp/ready", canonicalize);
        var c = LocalEndpointCache.KeyFor("http://192.168.0.111:8112/asr/ready", canonicalize);

        a.Should().Be(b, "same box + same path = one identity");
        a.Should().NotBe(c, "same box, different service endpoint = distinct identities");
        cache.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Unreachable_IsCACHED_AtLeastAsConfidently_AsReachable_NeverEvicted_FailuresAccumulate()
    {
        var now = DateTime.UtcNow;
        var clockState = now;
        var clock = new Func<DateTime>(() => clockState);
        var probe = new SequenceProbe(
            Unreachable("u", now),
            Unreachable("u", clockState),
            Unreachable("u", clockState),
            Reachable("u", clockState));
        var cache = CreateCache(probe, clock);
        var url = "http://max:8112/emb/ready";

        await cache.ObserveAsync(url);
        clockState = clockState.AddSeconds(60);
        await cache.ObserveAsync(url);
        clockState = clockState.AddSeconds(60);
        await cache.ObserveAsync(url);

        // The unreachable state survives 120 s (long past the 60 s reachable
        // validity) with the consecutive-failure count carried forward — an
        // unreachable engine is a stable, renderable state, not a blip.
        cache.TryGet(url, out var observation).Should().BeTrue();
        observation.Availability.Should().Be(EndpointAvailability.Unreachable);
        observation.ConsecutiveFailures.Should().Be(3);

        // Recovery flips the state to Reachable and resets the counter.
        await cache.ObserveAsync(url);
        cache.TryGet(url, out var recovered).Should().BeTrue();
        recovered.Availability.Should().Be(EndpointAvailability.Reachable);
        recovered.ConsecutiveFailures.Should().Be(0);
    }

    [TestMethod]
    public void SignalRefresh_WakesWaitForRefresh_Immediately()
    {
        var probe = new ThrowingIfTouchedProbe();
        var cache = CreateCache(probe);
        var url = "http://max:8112/sd/health";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = cache.WaitForRefreshAsync(cts.Token);
        wait.IsCompleted.Should().BeFalse();

        cache.SignalRefresh(url);

        wait.IsCompleted.Should().BeTrue();
    }

    [TestMethod]
    public void Snapshot_ReflectsMarkedObservations_KeyedByCanonicalKey()
    {
        var probe = new ThrowingIfTouchedProbe();
        Func<string, string> canonicalize = u => u;
        var cache = CreateCache(probe, null, canonicalize);
        var url = "http://max:8112/asr/ready";
        cache.MarkObserved(url, Reachable(url));

        var snapshot = cache.Snapshot();

        snapshot.Should().HaveCount(1);
        var (key, observation) = snapshot.Single();
        key.Should().Be(LocalEndpointCache.KeyFor(url, canonicalize));
        observation.Availability.Should().Be(EndpointAvailability.Reachable);
    }

    // ---- fakes ----

    private sealed class ThrowingIfTouchedProbe : ILocalEndpointProbe
    {
        public int Touches { get; private set; }
        public Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken)
        {
            Touches++;
            throw new InvalidOperationException("probe must not be touched by TryGet");
        }
    }

    private sealed class CountingProbe : ILocalEndpointProbe
    {
        private readonly Func<Task> _gate;
        private int _calls;
        public int Calls => _calls;
        public CountingProbe(Func<Task> gate) => _gate = gate;
        public async Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken)
        {
            System.Threading.Interlocked.Increment(ref _calls);
            // A true await: releases the calling thread so the 100 racers can
            // all register before the gate opens.
            await _gate().ConfigureAwait(false);
            return Reachable(readinessUrl);
        }
    }

    private sealed class SequenceProbe : ILocalEndpointProbe
    {
        private readonly Queue<EndpointObservation> _results;
        public SequenceProbe(params EndpointObservation[] results) => _results = new Queue<EndpointObservation>(results);
        public Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken) =>
            Task.FromResult(_results.Dequeue());
    }
}
