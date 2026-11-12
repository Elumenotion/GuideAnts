using FluentAssertions;
using GuideAntsApi.Services.EmbeddingsGate;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.EmbeddingsGate;

[TestClass]
public sealed class EmbeddingsReadinessGateTests
{
    [TestMethod]
    public async Task UsesLocalEmbeddings_True_WhenProviderSectionMatches()
    {
        var resolver = new Mock<IServiceModeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", null, null, true, true));

        var gate = CreateGate(resolver);

        var result = await gate.UsesLocalEmbeddingsAsync();
        result.Should().BeTrue();
    }

    [TestMethod]
    public async Task UsesLocalEmbeddings_False_WhenProviderSectionDoesNotMatch()
    {
        var resolver = new Mock<IServiceModeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceMode("azure", "AzureOpenAiEmbedding", null, null, true, true));

        var gate = CreateGate(resolver);

        var result = await gate.UsesLocalEmbeddingsAsync();
        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task UsesLocalEmbeddings_False_WhenRoutingException()
    {
        var resolver = new Mock<IServiceModeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RoutingException("ROUTING_MODE_NOT_FOUND", "no mode", "Pick a mode"));

        var gate = CreateGate(resolver);

        var result = await gate.UsesLocalEmbeddingsAsync();
        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task EnsureLoaded_ReturnsTrue_WhenAlreadyLoaded()
    {
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = true,
            });

        var gate = CreateGate(loadService: loadService.Object);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeTrue();
        error.Should().BeNull();
        loadService.Verify(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EnsureLoaded_CallsEnsureLoadedAsync_WhenNotLoaded()
    {
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = false,
            });
        loadService.Setup(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceOperationResult
            {
                ServiceId = "Embeddings",
                Success = true,
            });

        var gate = CreateGate(loadService: loadService.Object);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeTrue();
        error.Should().BeNull();
        loadService.Verify(s => s.EnsureLoadedAsync("Embeddings", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureLoaded_ReturnsFalse_WhenNotConfigured()
    {
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = false,
                Error = "No local admin base URL is configured.",
            });

        var gate = CreateGate(loadService: loadService.Object);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeFalse();
        error.Should().Be("No local admin base URL is configured.");
        loadService.Verify(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EnsureLoaded_ReturnsFalse_WhenEnsureLoadedFails()
    {
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = false,
            });
        loadService.Setup(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocalServiceOperationResult.Failure("Embeddings", "Load timed out"));

        var gate = CreateGate(loadService: loadService.Object);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeFalse();
        error.Should().Be("Load timed out");
    }

    [TestMethod]
    public async Task ConcurrentCallers_IssueSingleEnsureLoaded()
    {
        var loaded = false;
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = loaded,
            }));
        loadService.Setup(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => { loaded = true; })
            .Returns(() => Task.FromResult(new LocalServiceOperationResult
            {
                ServiceId = "Embeddings",
                Success = true,
            }));

        var gate = CreateGate(loadService: loadService.Object);

        // Fire 5 concurrent callers
        var tasks = Enumerable.Range(0, 5).Select(_ => gate.EnsureLocalEmbeddingsLoadedAsync()).ToList();
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.Loaded);
        loadService.Verify(s => s.EnsureLoadedAsync("Embeddings", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static EmbeddingsReadinessGate CreateGate(
        Mock<IServiceModeResolver>? resolver = null,
        ILocalServiceLoadService? loadService = null,
        ILocalInventoryMirror? inventoryMirror = null,
        ILocalEndpointCache? endpointCache = null,
        IConfiguration? configuration = null)
    {
        var resolverOwned = resolver is null;
        resolver ??= new Mock<IServiceModeResolver>();
        if (resolverOwned)
        {
            resolver
                .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", null, null, true, true));
        }

        loadService ??= new Mock<ILocalServiceLoadService>().Object;

        return new EmbeddingsReadinessGate(
            loadService,
            resolver.Object,
            configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<EmbeddingsReadinessGate>.Instance,
            inventoryMirror,
            endpointCache);
    }

    // ---- T-U9: reachable-but-absent → named ArtifactMissing, no load POST, latch free ----

    [TestMethod]
    public async Task EnsureLoaded_ReachableButSelectionAbsent_FailsNamed_WithoutIssuingLoad()
    {
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = false,
            });

        // Plane C: endpoint reachable (per /ready), listing last-known WITHOUT the selection.
        var endpointCache = new LocalEndpointCache(new NoopProbe2());
        endpointCache.MarkObserved("http://localhost:8110/emb/ready", new EndpointObservation(
            "http://localhost:8110/emb/ready", EndpointAvailability.Reachable, DateTime.UtcNow, 4, null, 0, "unloaded"));
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.Embeddings,
            [new NotebookToolbarLocalModelOptionDto("other-model", "other-model", true, false)], DateTime.UtcNow);

        var resolver = new Mock<IServiceModeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", "bge-small", null, true, true));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            })
            .Build();
        var gate = CreateGate(resolver, loadService.Object, mirror, endpointCache, config);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeFalse();
        error.Should().NotBeNull().And.Contain("RUNTIME_ARTIFACT_MISSING").And.Contain("bge-small");
        loadService.Verify(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the load that can never succeed must not be issued");
    }

    [TestMethod]
    public async Task EnsureLoaded_Unreachable_KeepsDeferAndRetry()
    {
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = false,
 });

        // Cache: endpoint UNREACHABLE — the artifact question is unanswerable, so today's
        // behavior (defer-and-retry via EnsureLoaded) is preserved; no artifact claim.
        var endpointCache = new LocalEndpointCache(new NoopProbe2());
        endpointCache.MarkObserved("http://localhost:8110/emb/ready", new EndpointObservation(
            "http://localhost:8110/emb/ready", EndpointAvailability.Unreachable, DateTime.UtcNow, 4, "refused", 1));
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.Embeddings, [], DateTime.UtcNow);

        var resolver = new Mock<IServiceModeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", "bge-small", null, true, true));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            })
            .Build();
        var gate = CreateGate(resolver, loadService.Object, mirror, endpointCache, config);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeFalse();
        loadService.Verify(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "a use path keeps its guard: unreachable stays defer-and-retry, never masquerades as a deleted model");
    }

    [TestMethod]
    public async Task EnsureLoaded_NoMirrorEvidence_KeepsCurrentBehavior()
    {
        // Without cache/mirror injected, the gate behaves exactly as before P5.
        var loadService = new Mock<ILocalServiceLoadService>();
        loadService.Setup(s => s.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceReadiness
            {
                ServiceId = "Embeddings",
                Configured = true,
                Loaded = false,
            });
        loadService.Setup(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalServiceOperationResult { ServiceId = "Embeddings", Success = true });

        var gate = CreateGate(loadService: loadService.Object);
        var (loaded, error) = await gate.EnsureLocalEmbeddingsLoadedAsync();

        loaded.Should().BeTrue();
        loadService.Verify(s => s.EnsureLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class NoopProbe2 : ILocalEndpointProbe
    {
        public Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("gate tests must not probe; seed the cache");
    }
}
