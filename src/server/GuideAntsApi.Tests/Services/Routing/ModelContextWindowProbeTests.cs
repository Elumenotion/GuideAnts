using System.Net;
using System.Text;
using AntRunner.Chat.Anthropic;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Moq;
using Moq.Protected;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ModelContextWindowProbeTests
{
    private static IModelContextWindowProbe Build(
        string responseJson,
        HttpStatusCode status = HttpStatusCode.OK,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) =>
            {
                onRequest?.Invoke(request);
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                });
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object));

        var providers = new Mock<IProviderConfigurationResolver>();
        providers.Setup(p => p.GetAnthropicConfig())
            .Returns(new AnthropicConfig { ApiKey = "test-key", BaseUrl = "https://api.anthropic.com" });
        providers.Setup(p => p.GetOpenRouterOptions())
            .Returns(new OpenRouterOptions { BaseUrl = "https://openrouter.ai/api/v1" });

        return new ModelContextWindowProbe(factory.Object, providers.Object);
    }

    [TestMethod]
    public async Task Anthropic_ParsesMaxInputAndMaxTokens()
    {
        var probe = Build("""
            {"id":"claude-haiku-4-5","max_input_tokens":200000,"max_tokens":64000}
            """);

        var result = await probe.ProbeAsync("claude-haiku-4-5", "anthropic", CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.ContextWindowTokens.Should().Be(200_000);
        result.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public async Task Anthropic_EscapesModelIdInRequestUri()
    {
        string? requested = null;
        var probe = Build(
            """{"max_input_tokens":1000,"max_tokens":100}""",
            onRequest: r => requested = r.RequestUri!.OriginalString);

        await probe.ProbeAsync("weird/id ?x=1#f", "anthropic", CancellationToken.None);

        requested.Should().Be("https://api.anthropic.com/v1/models/weird%2Fid%20%3Fx%3D1%23f");
    }

    [TestMethod]
    public async Task OpenRouter_ParsesContextLengthForMatchingId()
    {
        var probe = Build("""
            {"data":[
              {"id":"other/model","context_length":8192,
               "top_provider":{"max_completion_tokens":1024}},
              {"id":"minimax/minimax-m3","context_length":1000000,
               "top_provider":{"max_completion_tokens":128000}}
            ]}
            """);

        var result = await probe.ProbeAsync("minimax/minimax-m3", "openrouter-chat", CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.ContextWindowTokens.Should().Be(1_000_000);
        result.MaxOutputTokens.Should().Be(128_000);
    }

    [TestMethod]
    public async Task OpenRouter_PrefersTopProviderContextLength_WhenValuesDiffer()
    {
        var probe = Build("""
            {"data":[
              {"id":"vendor/model","context_length":2000000,
               "top_provider":{"context_length":1000000,"max_completion_tokens":32000}}
            ]}
            """);

        var result = await probe.ProbeAsync("vendor/model", "openrouter-chat", CancellationToken.None);

        result.ContextWindowTokens.Should().Be(1_000_000);
        result.MaxOutputTokens.Should().Be(32_000);
    }

    [TestMethod]
    public async Task OpenRouter_FallsBackToTopLevelContextLength_WhenTopProviderHasNone()
    {
        var probe = Build("""
            {"data":[
              {"id":"vendor/model","context_length":2000000,
               "top_provider":{"max_completion_tokens":32000}}
            ]}
            """);

        var result = await probe.ProbeAsync("vendor/model", "openrouter-chat", CancellationToken.None);

        result.ContextWindowTokens.Should().Be(2_000_000);
    }

    [TestMethod]
    public async Task OpenRouter_DropsMaxOutput_WhenNearlyEqualToWindow()
    {
        var probe = Build("""
            {"data":[
              {"id":"vendor/near","context_length":600000,
               "top_provider":{"context_length":524288,"max_completion_tokens":512000}}
            ]}
            """);

        var result = await probe.ProbeAsync("vendor/near", "openrouter-chat", CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.ContextWindowTokens.Should().Be(524_288);
        result.MaxOutputTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task OpenRouter_DropsMaxOutput_WhenEqualToWindow()
    {
        var probe = Build("""
            {"data":[
              {"id":"vendor/equal","context_length":98304,
               "top_provider":{"context_length":98304,"max_completion_tokens":98304}}
            ]}
            """);

        var result = await probe.ProbeAsync("vendor/equal", "openrouter-chat", CancellationToken.None);

        result.ContextWindowTokens.Should().Be(98_304);
        result.MaxOutputTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Anthropic_DropsMaxOutput_WhenLargerThanWindow()
    {
        var probe = Build("""
            {"id":"claude-odd","max_input_tokens":73728,"max_tokens":90112}
            """);

        var result = await probe.ProbeAsync("claude-odd", "anthropic", CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.ContextWindowTokens.Should().Be(73_728);
        result.MaxOutputTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task OpenRouter_KeepsCoherentPairUnchanged()
    {
        var probe = Build("""
            {"data":[
              {"id":"vendor/ok","context_length":262144,
               "top_provider":{"context_length":262144,"max_completion_tokens":65536}}
            ]}
            """);

        var result = await probe.ProbeAsync("vendor/ok", "openrouter-chat", CancellationToken.None);

        result.ContextWindowTokens.Should().Be(262_144);
        result.MaxOutputTokens.Should().Be(65_536);
        result.Message.Should().BeNull();
    }

    [TestMethod]
    public async Task OpenRouter_ReturnsNullValues_WhenModelIdNotListed()
    {
        var probe = Build("""{"data":[{"id":"other/model","context_length":8192}]}""");

        var result = await probe.ProbeAsync("missing/model", "openrouter-chat", CancellationToken.None);

        result.Supported.Should().BeTrue("the provider supports probing even when the model is absent");
        result.ContextWindowTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task UnsupportedProvider_ReportsUnsupported()
    {
        var probe = Build("{}");

        var result = await probe.ProbeAsync("gpt-4.1", "openai-chat", CancellationToken.None);

        result.Supported.Should().BeFalse();
        result.ContextWindowTokens.Should().BeNull();
        result.Message.Should().Contain("does not publish");
    }

    [TestMethod]
    public async Task HttpFailure_ReturnsMessageRatherThanThrowing()
    {
        var probe = Build("""{"error":"nope"}""", HttpStatusCode.Unauthorized);

        var result = await probe.ProbeAsync("claude-haiku-4-5", "anthropic", CancellationToken.None);

        result.ContextWindowTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }
}
