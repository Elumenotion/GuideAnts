using FluentAssertions;
using GuideAntsApi.Services.EmbeddingsGate;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
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
        ILocalServiceLoadService? loadService = null)
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
            NullLogger<EmbeddingsReadinessGate>.Instance);
    }
}
