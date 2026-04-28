using System.Net;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.HuggingFace;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Endpoints;

/// <summary>
/// Contract tests for the shared Hugging Face browse handler without needing
/// a full WebApplicationFactory host.
/// </summary>
[TestClass]
public sealed class HuggingFaceBrowseHandlerTests
{
    private sealed class StubBrowser : IHuggingFaceRepositoryBrowser
    {
        private readonly HuggingFaceRepositoryListing? _result;
        private readonly Exception? _error;

        public StubBrowser(HuggingFaceRepositoryListing result) { _result = result; }
        public StubBrowser(Exception error) { _error = error; }

        public Task<HuggingFaceRepositoryListing> ListFilesAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
        {
            if (_error is not null) throw _error;
            return Task.FromResult(_result!);
        }
    }

    private static HttpRequest BuildRequest(string? serviceOrigin = null)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(serviceOrigin))
        {
            context.Request.Headers[HuggingFaceBrowseHandler.ServiceOriginHeaderName] = serviceOrigin;
        }
        return context.Request;
    }

    private static ILoggerFactory LoggerFactory() => NullLoggerFactory.Instance;

    [TestMethod]
    public async Task CanonicalPath_WithServiceOrigin_ReturnsListing()
    {
        var listing = new HuggingFaceRepositoryListing(
            Repository: "acme/model",
            Gated: false,
            TokenUsed: false,
            ModelCardUrl: null,
            Files: Array.Empty<HuggingFaceRepositoryFile>());
        var browser = new StubBrowser(listing);

        var result = await HuggingFaceBrowseHandler.ExecuteAsync(
            owner: "acme",
            repo: "model",
            request: BuildRequest(serviceOrigin: "Embeddings"),
            browser: browser,
            loggerFactory: LoggerFactory(),
            cancellationToken: CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<HuggingFaceRepositoryListing>>();
    }

    [TestMethod]
    public async Task CanonicalPath_WithoutHeader_ReturnsListing()
    {
        var listing = new HuggingFaceRepositoryListing(
            Repository: "acme/model",
            Gated: false,
            TokenUsed: false,
            ModelCardUrl: null,
            Files: Array.Empty<HuggingFaceRepositoryFile>());
        var browser = new StubBrowser(listing);

        var result = await HuggingFaceBrowseHandler.ExecuteAsync(
            owner: "acme",
            repo: "model",
            request: BuildRequest(serviceOrigin: null),
            browser: browser,
            loggerFactory: LoggerFactory(),
            cancellationToken: CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<HuggingFaceRepositoryListing>>();
    }

    [TestMethod]
    public async Task StructuredBrowseException_IsEchoedAsJsonWithMatchingStatus()
    {
        var browser = new StubBrowser(new HuggingFaceBrowseException(
            code: "REPO_NOT_FOUND",
            message: "Repository 'acme/missing' was not found on Hugging Face.",
            statusCode: HttpStatusCode.NotFound));

        var result = await HuggingFaceBrowseHandler.ExecuteAsync(
            owner: "acme",
            repo: "missing",
            request: BuildRequest(serviceOrigin: "ImageGeneration"),
            browser: browser,
            loggerFactory: LoggerFactory(),
            cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("code").GetString().Should().Be("REPO_NOT_FOUND");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("not found");
    }
}
