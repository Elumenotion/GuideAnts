using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.Services.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Diagnostics;
using System.Net;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class DocumentServerHealthProbeTests
{
    private const string DefaultProbeUrl = "http://documentserver/web-apps/apps/api/documents/api.js";

    [TestMethod]
    public async Task IsReachableAsync_WhenDisabled_ReturnsFalseWithoutCallingDocumentServer()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = CreateProbe(handler, enabled: false);

        var reachable = await probe.IsReachableAsync(CancellationToken.None);

        reachable.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IsReachableAsync_WhenEditorScriptReturns200_ReturnsTrue()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = CreateProbe(handler);

        var reachable = await probe.IsReachableAsync(CancellationToken.None);

        reachable.Should().BeTrue();
        handler.Requests.Single().RequestUri.Should().Be(new Uri(DefaultProbeUrl));
    }

    [TestMethod]
    public async Task IsReachableAsync_WhenInternalUrlHasTrailingSlash_ProbesWithoutDoubleSlash()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = CreateProbe(handler, internalUrl: "http://documentserver:8000/");

        await probe.IsReachableAsync(CancellationToken.None);

        handler.Requests.Single().RequestUri.Should()
            .Be(new Uri("http://documentserver:8000/web-apps/apps/api/documents/api.js"));
    }

    [TestMethod]
    public async Task IsReachableAsync_WhenEditorScriptReturns500_ReturnsFalse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var probe = CreateProbe(handler);

        (await probe.IsReachableAsync(CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task IsReachableAsync_WhenConnectionFails_ReturnsFalse()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        var probe = CreateProbe(handler);

        (await probe.IsReachableAsync(CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task IsReachableAsync_WhenDocumentServerHangs_ReturnsFalseAfterTimeout()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var probe = CreateProbe(handler, probeTimeout: TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        var reachable = await probe.IsReachableAsync(CancellationToken.None);

        reachable.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task IsReachableAsync_DoesNotCacheSuccess_SoAStoppedContainerIsSeenOnTheNextCall()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable]);
        var handler = new StubHandler(_ => new HttpResponseMessage(statuses.Dequeue()));
        var probe = CreateProbe(handler);

        (await probe.IsReachableAsync(CancellationToken.None)).Should().BeTrue();
        (await probe.IsReachableAsync(CancellationToken.None)).Should().BeFalse();

        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task IsReachableAsync_CachesFailureForSubsequentCalls()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        var probe = CreateProbe(handler);

        (await probe.IsReachableAsync(CancellationToken.None)).Should().BeFalse();
        (await probe.IsReachableAsync(CancellationToken.None)).Should().BeFalse();

        handler.Requests.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task IsReachableAsync_WhenCallerCancels_ThrowsAndDoesNotCache()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cache = NewCache();
        var probe = CreateProbe(handler, cache: cache);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await probe.Invoking(p => p.IsReachableAsync(callerCancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        cache.TryGetValue(DocumentServerHealthProbe.CacheKey, out _).Should().BeFalse();
    }

    private static DocumentServerHealthProbe CreateProbe(
        StubHandler handler,
        bool enabled = true,
        string internalUrl = "http://documentserver",
        IMemoryCache? cache = null,
        TimeSpan? probeTimeout = null)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new DocumentServerHealthProbe(
            httpClientFactory.Object,
            cache ?? NewCache(),
            Microsoft.Extensions.Options.Options.Create(new DocumentServerOptions { Enabled = enabled, InternalUrl = internalUrl }),
            NullLogger<DocumentServerHealthProbe>.Instance,
            probeTimeout ?? DocumentServerHealthProbe.DefaultProbeTimeout);
    }

    // Mirrors the app's cache: SizeLimit set, so entries without Size would throw.
    private static MemoryCache NewCache() => new(new MemoryCacheOptions { SizeLimit = 1024 });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _responder(request, cancellationToken);
        }
    }
}
