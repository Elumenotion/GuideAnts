using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ServiceLocalModelListEnricherTests
{
    [TestMethod]
    public async Task ProxyAndEnrichImageBundlesAsync_ExposesSavedSelectionWithoutRewritingRuntimeState()
    {
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(service => service.GetServiceModesAsync("ImageGeneration", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    "ImageGeneration",
                    "local",
                    "LocalServiceHosts:ImageGenerationBaseUrl",
                    "saved-bundle",
                    null,
                    Enabled: true,
                    IsDefault: false),
            ]);

        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "activeBundleMarkerId":"engine-marker",
                  "loadedBundleId":null,
                  "items":[
                    {"bundleId":"saved-bundle","active":false},
                    {"bundleId":"engine-marker","active":true}
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"),
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://upstream/sd/admin/bundles");

        var result = await ServiceLocalModelListEnricher.ProxyAndEnrichImageBundlesAsync(
            client,
            request,
            settings.Object,
            CancellationToken.None);

        var (_, body) = await ExecuteResultAsync(result);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.GetProperty("selectedBundleId").GetString().Should().Be("saved-bundle");
        root.GetProperty("activeBundleMarkerId").GetString().Should().Be("engine-marker");
        root.TryGetProperty("activeBundleId", out _).Should().BeFalse();
        root.GetProperty("items")[0].GetProperty("active").GetBoolean().Should().BeFalse();
        root.GetProperty("items")[1].GetProperty("active").GetBoolean().Should().BeTrue();
        settings.Verify(
            service => service.SetServiceModeModelIdAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await result.ExecuteAsync(context);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
