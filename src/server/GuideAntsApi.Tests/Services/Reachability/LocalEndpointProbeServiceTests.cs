using FluentAssertions;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Reachability;

/// <summary>T-U2 — the probe loop: refused/blackhole → Unreachable inside the
/// 3 s budget; HTTP 503 from /ready → Reachable with RuntimeState from the body
/// (never Unreachable); warning logged only on transition; SignalRefresh honored;
/// in-flight aux load skipped; DocumentIntelligence asserted presence-only.</summary>
[TestClass]
public sealed class LocalEndpointProbeServiceTests
{
    private static IConfiguration BuildConfig(
        string? asr = null, string? tts = null, string? emb = null, string? sd = null,
        string? llama = null, string? docling = null)
    {
        var collection = new Dictionary<string, string?>
        {
            ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = asr,
            ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = tts,
            ["LocalServiceHosts:EmbeddingsBaseUrl"] = emb,
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = sd,
            ["LocalServiceHosts:DocumentIntelligenceBaseUrl"] = docling,
            ["LlamaCpp:BaseUrl"] = llama,
        };
        return new ConfigurationBuilder().AddInMemoryCollection(collection).Build();
    }

    private static (LocalEndpointProbeService service, LocalEndpointCache cache, LocalInventoryMirror mirror,
        Mock<ILocalServiceLoadService> loadService, List<string> observed) CreateService(
        IConfiguration config,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
        ILocalEndpointProbe? probeOverride = null)
    {
        var cache = new LocalEndpointCache(
            probeOverride ?? new LocalEndpointProbe(new HttpFactoryStub(responder)),
            null,
            Identity);
        var mirror = new LocalInventoryMirror();
        var loadService = new Mock<ILocalServiceLoadService>();
        var observed = new List<string>();
        var recorder = new RecorderProbe(observed);
        var services = new ServiceCollection();
        services.AddSingleton(cache);
        services.AddSingleton(mirror);
        services.AddSingleton<ILocalModelObservationRecorder>(recorder);
        services.AddSingleton(loadService.Object);
        services.AddSingleton(config);
        services.AddSingleton<IHttpClientFactory>(new HttpFactoryStub(responder));
        var provider = services.BuildServiceProvider();
        var scopeFactory = new ServiceScopeFactoryStub(provider.GetRequiredService<IServiceScopeFactory>());

        var service = new LocalEndpointProbeService(
            cache, mirror, loadService.Object,
            scopeFactory,
            config, NullLogger<LocalEndpointProbeService>.Instance);
        return (service, cache, mirror, loadService, observed);
    }

    // Deterministic keying for tests: host:port + path, no DNS. Mirrors what
    // CanonicalInstanceKey produces for a literal IP (the production default is
    // DNS-based; here we avoid sandbox-dependent name resolution).
    private static string Identity(string url)
    {
        var u = url.Trim().TrimEnd('/');
        var schemeEnd = u.IndexOf("://", StringComparison.Ordinal);
        var restStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        return u[restStart..];
    }

    private static HttpResponseMessage Respond(int status, string body)
    {
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);
        response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return response;
    }

    [TestMethod]
    public async Task RefusedEndpoint_IsUnreachable_AndRecordsVerdict()
    {
        var config = BuildConfig(asr: "http://127.0.0.1:1");
        var (service, cache, _, _, observed) = CreateService(config, _ => throw new HttpRequestException("connection refused"));

        await service.RunCycleAsync(CancellationToken.None);

        cache.TryGet("http://127.0.0.1:1/asr/ready", out var observation).Should().BeTrue();
        observation.Availability.Should().Be(EndpointAvailability.Unreachable);
        observed.Should().ContainSingle().Which.Should().Be("SpeechTranscription");
    }

    [TestMethod]
    public async Task Http503FromReady_IsReachable_WithRuntimeStateFromBody_NeverUnreachable()
    {
        var config = BuildConfig(asr: "http://127.0.0.1:8112");
        var (service, cache, mirror, loadService, _) = CreateService(config, req =>
        {
            // /ready answers 503 with a structured body while not loaded (the
            // documented engine behavior). The listing is 200.
            if (req.RequestUri!.AbsolutePath.EndsWith("/ready"))
            {
                return Task.FromResult(Respond(503, "{\"ready\":false,\"loaded\":false,\"loading\":false}"));
            }
            if (req.RequestUri.AbsolutePath.EndsWith("/admin/models"))
            {
                return Task.FromResult(Respond(200, "{\"modelDir\":\"/models\",\"items\":[{\"modelRef\":\"m1\",\"complete\":true,\"active\":false}]}"));
            }
            return Task.FromResult(Respond(404, "{}"));
        });

        await service.RunCycleAsync(CancellationToken.None);

        cache.TryGet("http://127.0.0.1:8112/asr/ready", out var observation).Should().BeTrue();
        observation.Availability.Should().Be(EndpointAvailability.Reachable,
            "503 is an answer from a reachable upstream — classifying it Unreachable is the §4.1 lie");
        observation.RuntimeState.Should().Be("unloaded");

        // In-flight skip did not fire (no load in flight).
        loadService.Verify(x => x.IsOperationInFlight(RoutedServiceNames.SpeechTranscription), Times.AtLeastOnce);

        // The mirror got the live listing for the service (shown, not hidden).
        var got = mirror.TryGet(RoutedServiceNames.SpeechTranscription, out var models, out var source, out var observedUtc);
        got.Should().BeTrue();
        source.Should().Be(InventorySource.Live);
        observedUtc.Should().NotBeNull();
        models.Should().ContainSingle().Which.ModelRef.Should().Be("m1");
    }

    [TestMethod]
    public async Task InFlightAuxLoad_IsSkipped_NotProbed()
    {
        var config = BuildConfig(asr: "http://127.0.0.1:2", emb: "http://127.0.0.1:3");
        var (service, cache, _, loadService, observed) = CreateService(config, _ => Task.FromResult(Respond(200, "{\"loaded\":true}")));
        loadService
            .Setup(x => x.IsOperationInFlight(It.IsAny<string>()))
            .Returns((string s) => s == RoutedServiceNames.SpeechTranscription);

        await service.RunCycleAsync(CancellationToken.None);

        cache.TryGet("http://127.0.0.1:2/asr/ready", out _).Should().BeFalse("the in-flight service was skipped, not probed");
        cache.TryGet("http://127.0.0.1:3/emb/ready", out _).Should().BeTrue("a non-in-flight service on the same host is still probed");
        observed.Should().Contain(RoutedServiceNames.Embeddings).And.NotContain(RoutedServiceNames.SpeechTranscription);
    }

    [TestMethod]
    public async Task DocumentIntelligence_IsPresenceOnly_NoInventoryClaim()
    {
        var config = BuildConfig(docling: "http://127.0.0.1:4");
        var (service, cache, mirror, _, _) = CreateService(config, req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/admin/models"))
            {
                throw new InvalidOperationException("DocumentIntelligence must never be inventory-listed");
            }
            return Task.FromResult(Respond(200, "{}"));
        });

        await service.RunCycleAsync(CancellationToken.None);

        cache.TryGet("http://127.0.0.1:4", out var observation).Should().BeTrue();
        observation.Availability.Should().Be(EndpointAvailability.Reachable);
        mirror.TryGet(RoutedServiceNames.DocumentIntelligence, out var models, out var source, out _).Should().BeFalse("presence-only: no inventory, no loaded claim");
        models.Should().BeEmpty();
        source.Should().Be(InventorySource.None);
    }

    [TestMethod]
    public async Task SignalRefresh_IsConsumedByTheLoop_NotAwaitedInline()
    {
        var config = BuildConfig(emb: "http://127.0.0.1:5");
        var (service, cache, _, _, _) = CreateService(config, _ => Task.FromResult(Respond(200, "{\"loaded\":true}")));

        // SignalRefresh must complete the wait without the caller awaiting a
        // probe (F-2 corollary 3: Refresh signals interest and never awaits).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = cache.WaitForRefreshAsync(cts.Token);
        wait.IsCompleted.Should().BeFalse();
        cache.SignalRefresh("http://127.0.0.1:5/emb/ready");
        wait.IsCompleted.Should().BeTrue();

        await service.RunCycleAsync(CancellationToken.None);
        cache.TryGet("http://127.0.0.1:5/emb/ready", out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task TransitionLogging_WarnsOnlyOnFirstUnreachable_ThenDebug()
    {
        var config = BuildConfig(emb: "http://127.0.0.1:6");
        var logger = new ListLogger<LocalEndpointProbeService>();
        var cache = new LocalEndpointCache(
            new LocalEndpointProbe(new HttpFactoryStub(_ => throw new HttpRequestException("refused"))),
            null,
            Identity);
        var mirror = new LocalInventoryMirror();
        var loadService = new Mock<ILocalServiceLoadService>();
        var services = new ServiceCollection();
        services.AddSingleton(cache);
        services.AddSingleton(mirror);
        services.AddSingleton(loadService.Object);
        services.AddSingleton(config);
        services.AddSingleton<IHttpClientFactory>(new HttpFactoryStub(_ => throw new HttpRequestException("refused")));
        var provider = services.BuildServiceProvider();
        var scopeFactory = new ServiceScopeFactoryStub(provider.GetRequiredService<IServiceScopeFactory>());
        var service = new LocalEndpointProbeService(
            cache, mirror, loadService.Object,
            scopeFactory,
            config, logger);

        await service.RunCycleAsync(CancellationToken.None);
        await service.RunCycleAsync(CancellationToken.None);

        logger.Warnings.Should().HaveCount(1,
            "the detailed warning fires on the FIRST transition into Unreachable; subsequent cycles are quiet");
    }

    [TestMethod]
    public void ParseRuntimeState_LoadedLoadingUnloaded_AndStatusShapes()
    {
        LocalEndpointProbe.ParseRuntimeState("{\"loaded\":true,\"loading\":false}").Should().Be("loaded");
        LocalEndpointProbe.ParseRuntimeState("{\"loaded\":false,\"loading\":true}").Should().Be("loading");
        LocalEndpointProbe.ParseRuntimeState("{\"loaded\":false,\"loading\":false}").Should().Be("unloaded");
        LocalEndpointProbe.ParseRuntimeState("{\"status\":\"ok\",\"loadedBundleId\":\"flux\"}").Should().Be("loaded");
        LocalEndpointProbe.ParseRuntimeState("{\"status\":\"unloaded\"}").Should().Be("unloaded");
        LocalEndpointProbe.ParseRuntimeState("{}").Should().Be("unloaded");
        LocalEndpointProbe.ParseRuntimeState("not json").Should().BeNull();
        LocalEndpointProbe.ParseRuntimeState(null).Should().BeNull();
    }

    // ---- fakes / test doubles ----

    private sealed class HttpFactoryStub : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public HttpFactoryStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;
        public HttpClient CreateClient(string name) => new(new StubHandler(_responder));
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
            public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _responder(request);
        }
    }

    private sealed class RecorderProbe : ILocalModelObservationRecorder
    {
        public List<string> Services { get; }
        public RecorderProbe(List<string> sink) => Services = sink;
        public Task RecordServiceAsync(string serviceId, bool reachable, bool? selectedArtifactPresent, string? error, DateTime observedUtc, CancellationToken cancellationToken = default)
        {
            Services.Add(serviceId);
            return Task.CompletedTask;
        }
    }

    private sealed class ServiceScopeFactoryStub : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        public ServiceScopeFactoryStub(IServiceScopeFactory inner) => _inner = inner;
        public IServiceScope CreateScope() => _inner.CreateScope();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogLevel> Warnings { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(logLevel);
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
