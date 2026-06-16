using AntRunner.Chat.OpenRouter;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class OpenRouterAttributionTests
{
    [TestMethod]
    public void Apply_SendsGuideAntsAttributionHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");

        OpenRouterAttribution.Apply(request);

        HeaderValue(request, "HTTP-Referer").Should().Be(OpenRouterAttribution.HttpReferer);
        HeaderValue(request, "X-OpenRouter-Title").Should().Be(OpenRouterAttribution.AppTitle);
        HeaderValue(request, "X-Title").Should().Be(OpenRouterAttribution.AppTitle);
        HeaderValue(request, "X-OpenRouter-Categories").Should().Be(OpenRouterAttribution.AppCategories);
    }

    private static string? HeaderValue(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
}
