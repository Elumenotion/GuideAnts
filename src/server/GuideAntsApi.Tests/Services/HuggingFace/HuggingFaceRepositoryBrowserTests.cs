using System.Net;
using System.Text;
using GuideAntsApi.Services.HuggingFace;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.HuggingFace;

[TestClass]
public sealed class HuggingFaceRepositoryBrowserTests
{
    private static HuggingFaceRepositoryBrowser CreateBrowser(
        CapturingHandler handler,
        string? token = null)
    {
        var httpClient = new HttpClient(handler);
        return new HuggingFaceRepositoryBrowser(
            httpClient,
            new StubTokenResolver(token),
            NullLogger<HuggingFaceRepositoryBrowser>.Instance);
    }

    [TestMethod]
    public async Task ListFilesAsync_BlankOwner_ThrowsRepoInvalid()
    {
        var handler = new CapturingHandler(_ => Json("[]"));
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync(" ", "repo"));

        ex.Code.Should().Be("REPO_INVALID");
        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Captured.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ListFilesAsync_BlankRepo_ThrowsRepoInvalid()
    {
        var handler = new CapturingHandler(_ => Json("[]"));
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "  "));

        ex.Code.Should().Be("REPO_INVALID");
        handler.Captured.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ListFilesAsync_ClassifiesFilesAndBuildsListing()
    {
        const string body = """
        [
          { "type": "file", "path": "model-Q4_K_M.gguf", "size": 1234 },
          { "type": "file", "path": "mmproj-model-f16.gguf", "size": 50 },
          { "type": "file", "path": "model-00001-of-00003.gguf", "size": 99 },
          { "type": "file", "path": "README.md", "size": 7 },
          { "type": "directory", "path": "subdir" },
          { "type": "file" }
        ]
        """;
        var handler = new CapturingHandler(_ => Json(body));
        var browser = CreateBrowser(handler);

        var listing = await browser.ListFilesAsync("Owner", "Repo");

        listing.Repository.Should().Be("Owner/Repo");
        listing.Gated.Should().BeFalse();
        listing.TokenUsed.Should().BeFalse();
        listing.ModelCardUrl.Should().Be("https://huggingface.co/Owner/Repo");
        listing.Files.Should().HaveCount(4);

        var quant = listing.Files.Single(f => f.Path == "model-Q4_K_M.gguf");
        quant.Category.Should().Be("gguf");
        quant.QuantLabel.Should().Be("Q4_K_M");
        quant.Sharded.Should().BeFalse();

        var mmproj = listing.Files.Single(f => f.Path == "mmproj-model-f16.gguf");
        mmproj.Category.Should().Be("mmproj");
        mmproj.QuantLabel.Should().BeNull();

        var sharded = listing.Files.Single(f => f.Path == "model-00001-of-00003.gguf");
        sharded.Category.Should().Be("gguf");
        sharded.Sharded.Should().BeTrue();
        sharded.ShardIndex.Should().Be(1);
        sharded.ShardTotal.Should().Be(3);

        var other = listing.Files.Single(f => f.Path == "README.md");
        other.Category.Should().Be("other");
    }

    [TestMethod]
    public async Task ListFilesAsync_TrimsOwnerRepoAndEscapesUrl()
    {
        var handler = new CapturingHandler(_ => Json("[]"));
        var browser = CreateBrowser(handler);

        await browser.ListFilesAsync("/Owner/", "/Repo/");

        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Uri.Should().Be("https://huggingface.co/api/models/Owner/Repo/tree/main?recursive=true");
    }

    [TestMethod]
    public async Task ListFilesAsync_WithToken_SendsAuthorizationHeaderAndMarksTokenUsed()
    {
        var handler = new CapturingHandler(_ => Json("[]"));
        var browser = CreateBrowser(handler, token: "hf-secret");

        var listing = await browser.ListFilesAsync("owner", "repo");

        listing.TokenUsed.Should().BeTrue();
        handler.Captured[0].AuthScheme.Should().Be("Bearer");
        handler.Captured[0].AuthParameter.Should().Be("hf-secret");
    }

    [TestMethod]
    public async Task ListFilesAsync_Unauthorized_WithoutToken_ThrowsTokenMissing()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("nope")
        });
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "repo"));

        ex.Code.Should().Be("REPO_TOKEN_MISSING");
        ex.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task ListFilesAsync_Forbidden_WithToken_ThrowsTokenInsufficient()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("denied")
        });
        var browser = CreateBrowser(handler, token: "hf-secret");

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "repo"));

        ex.Code.Should().Be("REPO_TOKEN_INSUFFICIENT");
        ex.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task ListFilesAsync_NotFound_ThrowsRepoNotFound()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("missing")
        });
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "repo"));

        ex.Code.Should().Be("REPO_NOT_FOUND");
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ListFilesAsync_ServerError_ThrowsUpstream()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "repo"));

        ex.Code.Should().Be("HF_UPSTREAM");
        ex.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [TestMethod]
    public async Task ListFilesAsync_NonArrayJson_ThrowsUpstream()
    {
        var handler = new CapturingHandler(_ => Json("{\"unexpected\":true}"));
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "repo"));

        ex.Code.Should().Be("HF_UPSTREAM");
        ex.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [TestMethod]
    public async Task ListFilesAsync_InvalidJson_ThrowsUpstream()
    {
        var handler = new CapturingHandler(_ => Json("not json {"));
        var browser = CreateBrowser(handler);

        var ex = await CatchAsync(() => browser.ListFilesAsync("owner", "repo"));

        ex.Code.Should().Be("HF_UPSTREAM");
    }

    [TestMethod]
    public async Task ListFilesAsync_FollowsPaginationLinkHeader()
    {
        var handler = new CapturingHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("tree/main?recursive=true"))
            {
                var response = Json("""[ { "type": "file", "path": "page1.gguf", "size": 1 } ]""");
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://huggingface.co/api/models/owner/repo/tree/main?cursor=PAGE2>; rel=\"next\"");
                return response;
            }

            return Json("""[ { "type": "file", "path": "page2.gguf", "size": 2 } ]""");
        });
        var browser = CreateBrowser(handler);

        var listing = await browser.ListFilesAsync("owner", "repo");

        handler.Captured.Should().HaveCount(2);
        listing.Files.Select(f => f.Path).Should().BeEquivalentTo("page1.gguf", "page2.gguf");
    }

    private static async Task<HuggingFaceBrowseException> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HuggingFaceBrowseException ex)
        {
            return ex;
        }

        throw new AssertFailedException("Expected HuggingFaceBrowseException was not thrown.");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubTokenResolver : IHuggingFaceTokenResolver
    {
        private readonly string? _token;
        public StubTokenResolver(string? token) => _token = token;
        public string? Resolve() => _token;
    }

    private sealed record CapturedRequest(string Uri, string? AuthScheme, string? AuthParameter);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<CapturedRequest> Captured { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(_responder(request));
        }
    }
}
