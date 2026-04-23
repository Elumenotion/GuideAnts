using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class LocalServiceAdminRoutingTests
{
    [TestMethod]
    public void ResolveAdminBase_ImageGeneration_AppendsSdPrefix()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://guideants-ai:80"
        });

        var result = LocalServiceAdminRouting.ResolveAdminBase("ImageGeneration", configuration);

        result.Should().Be("http://guideants-ai:80/sd");
    }

    [TestMethod]
    public void ResolveAdminBase_TrimsTrailingSlashOnHost()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://guideants-ai:80/"
        });

        var result = LocalServiceAdminRouting.ResolveAdminBase("ImageGeneration", configuration);

        result.Should().Be("http://guideants-ai:80/sd");
    }

    [TestMethod]
    public void ResolveAdminBase_SpeechTranscription_AppendsAsrPrefix()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://guideants-ai:80"
        });

        var result = LocalServiceAdminRouting.ResolveAdminBase("SpeechTranscription", configuration);

        result.Should().Be("http://guideants-ai:80/asr");
    }

    [TestMethod]
    public void ResolveAdminBase_SpeechSynthesis_AppendsTtsPrefix()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://guideants-ai:80"
        });

        var result = LocalServiceAdminRouting.ResolveAdminBase("SpeechSynthesis", configuration);

        result.Should().Be("http://guideants-ai:80/tts");
    }

    [TestMethod]
    public void ResolveAdminBase_Embeddings_AppendsEmbPrefix()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://guideants-ai:80"
        });

        var result = LocalServiceAdminRouting.ResolveAdminBase("Embeddings", configuration);

        result.Should().Be("http://guideants-ai:80/emb");
    }

    [TestMethod]
    public void ResolveAdminBase_UnknownService_ReturnsNull()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>());

        LocalServiceAdminRouting.ResolveAdminBase("Chat", configuration).Should().BeNull();
    }

    [TestMethod]
    public void ResolveAdminBase_MissingHost_ReturnsNull()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            // intentionally no LocalServiceHosts:ImageGenerationBaseUrl
        });

        LocalServiceAdminRouting.ResolveAdminBase("ImageGeneration", configuration).Should().BeNull();
    }

    [TestMethod]
    public void AdminPrefix_KnownServices_ReturnExpectedValues()
    {
        LocalServiceAdminRouting.AdminPrefix("ImageGeneration").Should().Be("/sd");
        LocalServiceAdminRouting.AdminPrefix("SpeechTranscription").Should().Be("/asr");
        LocalServiceAdminRouting.AdminPrefix("SpeechSynthesis").Should().Be("/tts");
        LocalServiceAdminRouting.AdminPrefix("Embeddings").Should().Be("/emb");
    }

    [TestMethod]
    public void TryGetNonEmptyString_ReturnsTrimmedValue_WhenPresent()
    {
        var payload = JsonDocument.Parse("{\"model_id\":\"  openai/whisper-large-v3  \"}").RootElement;

        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var value).Should().BeTrue();
        value.Should().Be("openai/whisper-large-v3");
    }

    [TestMethod]
    public void TryGetNonEmptyString_ReturnsFalse_WhenMissing()
    {
        var payload = JsonDocument.Parse("{\"other\":\"x\"}").RootElement;

        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var value).Should().BeFalse();
        value.Should().BeEmpty();
    }

    [TestMethod]
    public void TryGetNonEmptyString_ReturnsFalse_WhenEmptyOrWhitespace()
    {
        var payload = JsonDocument.Parse("{\"a\":\"\",\"b\":\"   \"}").RootElement;

        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "a", out _).Should().BeFalse();
        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "b", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryGetNonEmptyString_ReturnsFalse_WhenNotAString()
    {
        var payload = JsonDocument.Parse("{\"n\":123,\"o\":{}}").RootElement;

        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "n", out _).Should().BeFalse();
        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "o", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryGetNonEmptyString_ReturnsFalse_WhenRootNotObject()
    {
        var payload = JsonDocument.Parse("[\"x\"]").RootElement;

        LocalServiceAdminRouting.TryGetNonEmptyString(payload, "0", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task BuildForwardedBodyWithHfToken_StampsInResolvedToken()
    {
        var payload = JsonDocument.Parse("{\"model_id\":\"owner/repo\"}").RootElement;

        using var content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, "hf_secret");
        var rendered = await content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.GetProperty("model_id").GetString().Should().Be("owner/repo");
        doc.RootElement.GetProperty("hf_token").GetString().Should().Be("hf_secret");
    }

    [TestMethod]
    public async Task BuildForwardedBodyWithHfToken_OverwritesClientSuppliedToken()
    {
        // Clients cannot bypass the single-source contract by smuggling a
        // different hf_token in the request body — the server-resolved value
        // always wins.
        var payload = JsonDocument.Parse("{\"model_id\":\"owner/repo\",\"hf_token\":\"attacker\"}").RootElement;

        using var content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, "hf_real");
        var rendered = await content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.GetProperty("hf_token").GetString().Should().Be("hf_real");
    }

    [TestMethod]
    public async Task BuildForwardedBodyWithHfToken_OmitsPropertyWhenResolvedTokenIsNull()
    {
        // When no token is configured the forwarded body MUST NOT contain
        // hf_token at all — downstream Pydantic validators treat missing and
        // empty differently, and the service sees "no token configured" as
        // a meaningful "let the HF call 401/403" signal.
        var payload = JsonDocument.Parse("{\"model_id\":\"owner/repo\",\"hf_token\":\"attacker\"}").RootElement;

        using var content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, null);
        var rendered = await content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.TryGetProperty("hf_token", out _).Should().BeFalse();
        doc.RootElement.GetProperty("model_id").GetString().Should().Be("owner/repo");
    }

    [TestMethod]
    public async Task BuildForwardedBodyWithHfToken_OmitsPropertyWhenResolvedTokenIsWhitespace()
    {
        var payload = JsonDocument.Parse("{\"model_id\":\"owner/repo\"}").RootElement;

        using var content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, "   ");
        var rendered = await content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.TryGetProperty("hf_token", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task ProxyAsync_PassesThroughBodyAndStatus_For200()
    {
        var handler = new StubHandler((request, _) =>
        {
            request.RequestUri!.ToString().Should().Be("http://upstream/sd/admin/bundles");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var client = new HttpClient(handler);

        var (statusCode, contentType, body) = await InvokeProxyAsync(
            client,
            new HttpRequestMessage(HttpMethod.Get, "http://upstream/sd/admin/bundles"));

        statusCode.Should().Be(StatusCodes.Status200OK);
        contentType.Should().StartWith("application/json");
        body.Should().Be("{\"items\":[]}");
    }

    [TestMethod]
    public async Task ProxyAsync_PassesThroughBodyAndStatus_For503()
    {
        // /ready returns 503 with a JSON payload when the model is not loaded.
        // That is a legitimate answer and must reach the client verbatim, not
        // be wrapped in the bad-gateway envelope.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"ready\":false}", System.Text.Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);

        var (statusCode, contentType, body) = await InvokeProxyAsync(
            client,
            new HttpRequestMessage(HttpMethod.Get, "http://upstream/asr/ready"));

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        contentType.Should().StartWith("application/json");
        body.Should().Be("{\"ready\":false}");
    }

    [TestMethod]
    public async Task ProxyAsync_WrapsUpstream404InBadGatewayEnvelope()
    {
        // An upstream 404 used to leak through as a raw 404 from the .NET
        // server, which the client then mistook for "the settings endpoint
        // doesn't exist" and rendered as the misleading "not available for
        // this deployment" message. The proxy now wraps every non-passthrough
        // upstream response in a 502 envelope carrying the full proxy chain.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"detail\":\"Not Found\"}", System.Text.Encoding.UTF8, "application/json"),
            ReasonPhrase = "Not Found",
        });
        using var client = new HttpClient(handler);

        var (statusCode, contentType, body) = await InvokeProxyAsync(
            client,
            new HttpRequestMessage(HttpMethod.Get, "http://upstream/sd/admin/bundles"));

        statusCode.Should().Be(StatusCodes.Status502BadGateway);
        contentType.Should().StartWith("application/json");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("upstreamTarget").GetString().Should().Be("http://upstream/sd/admin/bundles");
        root.GetProperty("upstreamStatus").GetInt32().Should().Be(404);
        root.GetProperty("upstreamStatusText").GetString().Should().Be("Not Found");
        root.GetProperty("upstreamContentType").GetString().Should().Contain("application/json");
        root.GetProperty("upstreamBody").GetString().Should().Be("{\"detail\":\"Not Found\"}");
        root.GetProperty("error").GetString().Should().Contain("http://upstream/sd/admin/bundles");
        root.GetProperty("error").GetString().Should().Contain("404");
    }

    [TestMethod]
    public async Task ProxyAsync_WrapsNetworkError_AsBadGatewayWithZeroStatus()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(handler);

        var (statusCode, contentType, body) = await InvokeProxyAsync(
            client,
            new HttpRequestMessage(HttpMethod.Get, "http://upstream/sd/admin/bundles"));

        statusCode.Should().Be(StatusCodes.Status502BadGateway);
        contentType.Should().StartWith("application/json");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("upstreamTarget").GetString().Should().Be("http://upstream/sd/admin/bundles");
        root.GetProperty("upstreamStatus").GetInt32().Should().Be(0);
        root.GetProperty("upstreamStatusText").GetString().Should().Be("NetworkError");
        root.GetProperty("error").GetString().Should().Contain("connection refused");
    }

    /// <summary>
    /// Invokes <see cref="LocalServiceAdminRouting.ProxyAsync"/> and executes
    /// the returned <see cref="IResult"/> against a dummy HTTP context,
    /// returning the resulting status code, content-type, and body. This lets
    /// the tests assert on the exact shape the client will see without
    /// reaching into Results-specific concrete types.
    /// </summary>
    private static async Task<(int statusCode, string contentType, string body)> InvokeProxyAsync(
        HttpClient httpClient,
        HttpRequestMessage request)
    {
        var result = await LocalServiceAdminRouting.ProxyAsync(httpClient, request, CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;

        await result.ExecuteAsync(ctx);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        var body = await reader.ReadToEndAsync();
        var contentType = ctx.Response.ContentType ?? string.Empty;
        return (ctx.Response.StatusCode, contentType, body);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        {
            _respond = respond;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_respond(request, cancellationToken));
        }
    }

    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
