using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiStartupWarmupServiceTests
{
    [TestMethod]
    public async Task EnsureAuxiliaryServicesLoadedAsync_EmbeddingsWithNoActiveModel_SendsEmptyLoadBodyForContainerDefault()
    {
        string? capturedEmbLoadBody = null;
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/emb/admin/load", StringComparison.Ordinal))
            {
                capturedEmbLoadBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/emb/admin/models", StringComparison.Ordinal))
            {
                const string modelsJson = """
                    {
                      "items": [
                        { "modelRef": "qwen3_embedding_0_6b", "isDirectory": true, "active": false },
                        { "modelRef": ".cache", "isDirectory": true, "active": false }
                      ]
                    }
                    """;
                return Json(HttpStatusCode.OK, modelsJson);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/emb/ready", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
                ["GA_EMB_READY_TIMEOUT_SECONDS"] = "10",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.Embeddings, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.SpeechTranscription, new ServiceMode(
                ModeId: "default",
                ProviderSection: "SpeechTranscription.Azure",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.SpeechSynthesis, new ServiceMode(
                ModeId: "default",
                ProviderSection: "SpeechSynthesis.Azure",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.ImageGeneration, new ServiceMode(
                ModeId: "default",
                ProviderSection: "ImageGeneration.Remote",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(),
            new StubHttpClientFactory(handler),
            new Mock<ILlamaRuntimeCoordinator>().Object,
            modeResolver,
            NullLogger<LocalAiStartupWarmupService>.Instance);

        await service.EnsureAuxiliaryServicesLoadedAsync();

        capturedEmbLoadBody.Should().NotBeNull();
        capturedEmbLoadBody.Should().Be("{}");
    }

    [TestMethod]
    public async Task EnsureAuxiliaryServicesLoadedAsync_EmbeddingsWithActiveModel_SendsThatModelPath()
    {
        string? capturedEmbLoadBody = null;
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/emb/admin/load", StringComparison.Ordinal))
            {
                capturedEmbLoadBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/emb/admin/models", StringComparison.Ordinal))
            {
                const string modelsJson = """
                    {
                      "items": [
                        { "modelRef": "qwen3_embedding_0_6b", "isDirectory": true, "active": true }
                      ]
                    }
                    """;
                return Json(HttpStatusCode.OK, modelsJson);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/emb/ready", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
                ["GA_EMB_READY_TIMEOUT_SECONDS"] = "10",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.Embeddings, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.SpeechTranscription, new ServiceMode(
                ModeId: "default",
                ProviderSection: "SpeechTranscription.Azure",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.SpeechSynthesis, new ServiceMode(
                ModeId: "default",
                ProviderSection: "SpeechSynthesis.Azure",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.ImageGeneration, new ServiceMode(
                ModeId: "default",
                ProviderSection: "ImageGeneration.Remote",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(),
            new StubHttpClientFactory(handler),
            new Mock<ILlamaRuntimeCoordinator>().Object,
            modeResolver,
            NullLogger<LocalAiStartupWarmupService>.Instance);

        await service.EnsureAuxiliaryServicesLoadedAsync();

        capturedEmbLoadBody.Should().Contain("qwen3_embedding_0_6b");
        capturedEmbLoadBody.Should().NotContain(".cache");
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ServiceScopeFactoryStub : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException("Not required for this test.");
    }
}
