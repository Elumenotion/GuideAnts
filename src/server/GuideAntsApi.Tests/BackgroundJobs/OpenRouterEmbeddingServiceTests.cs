using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.BackgroundJobs;

/// <summary>
/// Branch coverage for the OpenRouter embedding service using a fake HttpMessageHandler.
/// </summary>
[TestClass]
public sealed class OpenRouterEmbeddingServiceTests
{
    [TestMethod]
    public async Task GetEmbeddingsAsync_PurposeOverload_ThrowsBecauseModelIdRequired()
    {
        var handler = new CapturingHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var act = async () => await service.GetEmbeddingsAsync(["text"], EmbeddingPurpose.Document);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*require an explicit model id*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_ReturnsEmpty_WhenNoInputs()
    {
        var handler = new CapturingHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var result = await service.GetEmbeddingsAsync(Array.Empty<string>(), "text-embed", requestPresetJson: null);

        result.Should().BeEmpty();
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_Throws_WhenModelIdMissing()
    {
        var handler = new CapturingHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var act = async () => await service.GetEmbeddingsAsync(["text"], "   ", requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*model id is required*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_Throws_WhenApiKeyMissing()
    {
        var handler = new CapturingHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(
            httpClient,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

        var act = async () => await service.GetEmbeddingsAsync(["text"], "text-embed", requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OpenRouter:ApiKey is required*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_SingleInput_SendsScalarInputAndAttributionHeaders()
    {
        var handler = new CapturingHandler(_ => Json("{\"data\":[{\"embedding\":[0.1,0.2,0.3]}]}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var result = await service.GetEmbeddingsAsync(["only one"], "text-embed", requestPresetJson: null);

        result.Should().HaveCount(1);
        result[0].Should().Equal(0.1f, 0.2f, 0.3f);

        handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/embeddings");
        handler.LastAuthorizationParameter.Should().Be("or-key");
        handler.HeaderValue("HTTP-Referer").Should().Be("https://example.test");
        handler.HeaderValue("X-Title").Should().Be("GuideAnts");

        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("model").GetString().Should().Be("text-embed");
        requestJson.RootElement.GetProperty("input").ValueKind.Should().Be(JsonValueKind.String);
        requestJson.RootElement.GetProperty("input").GetString().Should().Be("only one");
        requestJson.RootElement.TryGetProperty("dimensions", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_MultipleInputs_SendsArrayInputAndDimensions()
    {
        var handler = new CapturingHandler(_ => Json("{\"data\":[{\"embedding\":[1.0]},{\"embedding\":[2.0]}]}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var result = await service.GetEmbeddingsAsync(["a", "b"], "text-embed", requestPresetJson: "{\"Dimensions\":256}");

        result.Should().HaveCount(2);

        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Array);
        requestJson.RootElement.GetProperty("input").GetArrayLength().Should().Be(2);
        requestJson.RootElement.GetProperty("dimensions").GetInt32().Should().Be(256);
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_AppliesDimensions_WhenPresetIsStringNumber()
    {
        var handler = new CapturingHandler(_ => Json("{\"data\":[{\"embedding\":[1.0]}]}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: "{\"Dimensions\":\"512\"}");

        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("dimensions").GetInt32().Should().Be(512);
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_IgnoresDimensions_WhenPresetInvalidOrNonPositive()
    {
        var handler = new CapturingHandler(_ => Json("{\"data\":[{\"embedding\":[1.0]}]}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: "{\"Dimensions\":0}");
        using (var first = JsonDocument.Parse(handler.LastRequestBody))
        {
            first.RootElement.TryGetProperty("dimensions", out _).Should().BeFalse();
        }

        await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: "not-json");
        using var second = JsonDocument.Parse(handler.LastRequestBody);
        second.RootElement.TryGetProperty("dimensions", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_Throws_WhenResponseNotSuccess()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited", Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var act = async () => await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*embeddings request failed (429): rate limited*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_Throws_WhenResponseBodyIsNull()
    {
        var handler = new CapturingHandler(_ => Json("null"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var act = async () => await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*response was empty*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_Throws_WhenResponseHasNoVectors()
    {
        var handler = new CapturingHandler(_ => Json("{\"data\":[]}"));
        using var httpClient = new HttpClient(handler);
        var service = new OpenRouterEmbeddingService(httpClient, BuildConfiguration());

        var act = async () => await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not contain any vectors*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_UsesConfiguredBaseUrl_WithTrailingSlashTrimmed()
    {
        var handler = new CapturingHandler(_ => Json("{\"data\":[{\"embedding\":[1.0]}]}"));
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:BaseUrl"] = "https://proxy.internal/api/"
            })
            .Build();
        var service = new OpenRouterEmbeddingService(httpClient, configuration);

        await service.GetEmbeddingsAsync(["a"], "text-embed", requestPresetJson: null);

        handler.LastRequestUri!.ToString().Should().Be("https://proxy.internal/api/embeddings");
        handler.HeaderValue("HTTP-Referer").Should().BeNull();
        handler.HeaderValue("X-Title").Should().BeNull();
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:HttpReferer"] = "https://example.test",
                ["OpenRouter:AppTitle"] = "GuideAnts"
            })
            .Build();

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public string? LastAuthorizationParameter { get; private set; }

        public string? HeaderValue(string name) => _headers.TryGetValue(name, out var value) ? value : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            _headers.Clear();
            foreach (var header in request.Headers)
            {
                _headers[header.Key] = string.Join(",", header.Value);
            }

            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
