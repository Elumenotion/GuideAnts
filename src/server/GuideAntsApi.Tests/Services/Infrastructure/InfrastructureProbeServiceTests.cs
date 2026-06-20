using System.Net;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Infrastructure;

/// <summary>
/// Phase E (R-5.7): reachability probe semantics for the Infrastructure tab.
/// Probes are caller-driven (items-in) so the service never re-reads
/// <c>IConfiguration</c>. URL items use GET + Range: bytes=0-0 (not HEAD);
/// path items get exists/writable checks.
/// </summary>
[TestClass]
public sealed class InfrastructureProbeServiceTests
{
    [TestMethod]
    public async Task ProbeAsync_UrlReturns200_IsReachableWithStatusCodeAndLatency()
    {
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler);

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("LocalServiceHosts:EmbeddingsBaseUrl", "url", "http://localhost:8110"),
        });

        batch.Results.Should().HaveCount(1);
        var result = batch.Results[0];
        result.Kind.Should().Be("url");
        result.Reachable.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.LatencyMs.Should().NotBeNull();
        result.Error.Should().BeNull();
        result.Exists.Should().BeNull();
        result.Writable.Should().BeNull();
    }

    [TestMethod]
    public async Task ProbeAsync_UrlTransportFailure_IsNotReachableAndCarriesError()
    {
        var handler = new StaticResponseHandler(_ =>
            throw new HttpRequestException("connection refused"));
        var service = CreateService(handler);

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("LocalServiceHosts:EmbeddingsBaseUrl", "url", "http://localhost:8110"),
        });

        var result = batch.Results.Single();
        result.Reachable.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task ProbeAsync_UrlMalformedScheme_ShortCircuitsWithoutConsumingTimeoutBudget()
    {
        var handler = new StaticResponseHandler(_ =>
        {
            Assert.Fail("HTTP handler must not be invoked for malformed URLs.");
            return new HttpResponseMessage();
        });
        var service = CreateService(handler);

        var start = DateTime.UtcNow;
        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("LlamaCpp:BaseUrl", "url", "not-a-url"),
        });
        var elapsed = DateTime.UtcNow - start;

        elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        var result = batch.Results.Single();
        result.Reachable.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task ProbeAsync_UrlReturns5xx_IsNotReachable_StatusCodePreserved()
    {
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler);

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("LocalServiceHosts:EmbeddingsBaseUrl", "url", "http://localhost:8110"),
        });

        var result = batch.Results.Single();
        result.Reachable.Should().BeFalse();
        result.StatusCode.Should().Be(500);
        result.Error.Should().Contain("500");
    }

    [TestMethod]
    public async Task ProbeAsync_UrlReturns4xx_IsReachable_BecauseServerResponded()
    {
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(handler);

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("LocalServiceHosts:EmbeddingsBaseUrl", "url", "http://localhost:8110"),
        });

        var result = batch.Results.Single();
        result.Reachable.Should().BeTrue();
        result.StatusCode.Should().Be(404);
    }

    [TestMethod]
    public async Task ProbeAsync_UrlUsesGetWithRangeHeader_SingleRequest()
    {
        var requestsSeen = new List<(HttpMethod Method, string? Range, string RequestUri)>();
        var handler = new StaticResponseHandler(request =>
        {
            requestsSeen.Add((request.Method, request.Headers.Range?.ToString(), request.RequestUri?.ToString() ?? ""));
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.Range.Should().NotBeNull();
            return new HttpResponseMessage(HttpStatusCode.PartialContent);
        });
        var service = CreateService(handler);

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("LlamaCpp:BaseUrl", "url", "http://localhost:8080/llama-cpp"),
        });

        requestsSeen.Should().HaveCount(1);
        requestsSeen[0].RequestUri.Should().Be("http://localhost:8080/llama-cpp/health");

        var result = batch.Results.Single();
        result.Reachable.Should().BeTrue();
        result.StatusCode.Should().Be(206);
    }

    [TestMethod]
    public void ToLlamaCppHealthProbeUri_AppendsHealthSegment()
    {
        InfrastructureProbeService.ToLlamaCppHealthProbeUri(new Uri("http://host:8110/llama-cpp"))
            .Should().Be(new Uri("http://host:8110/llama-cpp/health"));
        InfrastructureProbeService.ToLlamaCppHealthProbeUri(new Uri("http://host:8110/llama-cpp/"))
            .Should().Be(new Uri("http://host:8110/llama-cpp/health"));
        InfrastructureProbeService.ToLlamaCppHealthProbeUri(new Uri("http://guideants-ai:80/llama-cpp"))
            .Should().Be(new Uri("http://guideants-ai:80/llama-cpp/health"));
    }

    [TestMethod]
    public async Task ProbeAsync_DoclingBaseUrl_UsesVersionEndpoint()
    {
        var requestsSeen = new List<string>();
        var handler = new StaticResponseHandler(request =>
        {
            requestsSeen.Add(request.RequestUri?.ToString() ?? string.Empty);
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.Range.Should().NotBeNull();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = CreateService(handler);

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto(
                "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                "url",
                "http://docling-serve:5001"),
        });

        requestsSeen.Should().ContainSingle()
            .Which.Should().Be("http://docling-serve:5001/version");
        batch.Results.Single().Reachable.Should().BeTrue();
    }

    [TestMethod]
    public void ToDoclingVersionProbeUri_AppendsVersionSegment()
    {
        InfrastructureProbeService.ToDoclingVersionProbeUri(new Uri("http://docling-serve:5001"))
            .Should().Be(new Uri("http://docling-serve:5001/version"));
        InfrastructureProbeService.ToDoclingVersionProbeUri(new Uri("http://docling-serve:5001/"))
            .Should().Be(new Uri("http://docling-serve:5001/version"));
    }

    [TestMethod]
    public async Task ProbeAsync_PathExistingDirectory_ReturnsExistsAndWritable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guideants-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var service = CreateService(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

            var batch = await service.ProbeAsync(new[]
            {
                new InfrastructureProbeRequestItemDto("Test:ProbeTempDir", "path", tempDir),
            });

            var result = batch.Results.Single();
            result.Kind.Should().Be("path");
            result.Exists.Should().BeTrue();
            result.Writable.Should().BeTrue();
            result.Error.Should().BeNull();
            result.Reachable.Should().BeNull();
            result.StatusCode.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProbeAsync_PathExistingFile_ReturnsExistsTrueWritableFalse()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "guideants-probe-" + Guid.NewGuid().ToString("N") + ".ini");
        await File.WriteAllTextAsync(tempFile, "# sentinel");

        try
        {
            var service = CreateService(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

            var batch = await service.ProbeAsync(new[]
            {
                new InfrastructureProbeRequestItemDto("Test:ProbeTempFile", "path", tempFile),
            });

            var result = batch.Results.Single();
            result.Exists.Should().BeTrue();
            // Files don't get directory-write probing; writable is reported as
            // false so the UI can distinguish "file exists" from "directory-writable".
            result.Writable.Should().BeFalse();
            result.Error.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [TestMethod]
    public async Task ProbeAsync_PathMissing_ReturnsExistsFalseWithError()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "guideants-missing-" + Guid.NewGuid().ToString("N"));
        var service = CreateService(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("Test:ProbeMissingPath", "path", nonExistent),
        });

        var result = batch.Results.Single();
        result.Exists.Should().BeFalse();
        result.Writable.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task ProbeAsync_UnsupportedKind_ReturnsErrorRowWithoutThrowing()
    {
        var service = CreateService(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var batch = await service.ProbeAsync(new[]
        {
            new InfrastructureProbeRequestItemDto("Some:Key", "dns", "localhost"),
        });

        var result = batch.Results.Single();
        result.Error.Should().Contain("unsupported");
        result.Reachable.Should().BeNull();
        result.Exists.Should().BeNull();
    }

    private static InfrastructureProbeService CreateService(HttpMessageHandler handler)
    {
        var httpClientFactory = new StubHttpClientFactory(handler);
        return new InfrastructureProbeService(
            httpClientFactory,
            NullLogger<InfrastructureProbeService>.Instance);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
