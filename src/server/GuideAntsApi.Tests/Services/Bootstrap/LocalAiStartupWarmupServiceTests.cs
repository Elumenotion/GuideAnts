using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
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

    [TestMethod]
    public async Task EnsureAuxiliaryServicesLoadedAsync_FailedLoadHttp500_DoesNotRetryAndContinuesToNextService()
    {
        var asrLoadAttempts = 0;
        var embLoadAttempts = 0;
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/asr/admin/load", StringComparison.Ordinal))
            {
                asrLoadAttempts++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        """{"status":"failed","error":"model_load_failed"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/asr/admin/models", StringComparison.Ordinal))
            {
                const string modelsJson = """
                    {
                      "items": [
                        { "modelRef": "Qwen3-ASR-0.6B", "isDirectory": true, "active": true }
                      ]
                    }
                    """;
                return Json(HttpStatusCode.OK, modelsJson);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/emb/admin/load", StringComparison.Ordinal))
            {
                embLoadAttempts++;
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
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
                ["GA_ASR_READY_TIMEOUT_SECONDS"] = "10",
                ["GA_EMB_READY_TIMEOUT_SECONDS"] = "10",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.SpeechTranscription, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.Embeddings, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
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

        asrLoadAttempts.Should().Be(1);
        embLoadAttempts.Should().Be(1);
    }

    [TestMethod]
    public async Task ReconcileLocalServiceAsync_SelectActive_AutoActivatesLocalProvider_WhenRoutingMissing()
    {
        var selectActiveCalled = false;
        var loadCalled = false;
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path.EndsWith("/sd/admin/bundles/flux2-klein-4b-q4ks/select-active", StringComparison.Ordinal))
            {
                selectActiveCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/sd/admin/load", StringComparison.Ordinal))
            {
                loadCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/sd/health", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"engine":{"processAlive":true,"healthy":true}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
                ["GA_SD_READY_TIMEOUT_SECONDS"] = "10",
            })
            .Build();

        var routingGate = new RoutingActivationGate();
        var modeResolver = new GatedServiceModeResolver(
            routingGate,
            RoutedServiceNames.ImageGeneration,
            new ServiceMode(
                ModeId: "local",
                ProviderSection: "LocalServiceHosts:ImageGenerationBaseUrl",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true));

        var settingsMock = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settingsMock
            .Setup(s => s.EnsureServiceModeExistsAsync(
                ImageGenerationOptions.SectionName,
                ServiceProviderIds.ImageGenerationLocalSdHttp,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => routingGate.Activate());
        settingsMock
            .Setup(s => s.SetServiceActiveProviderAsync(
                ImageGenerationOptions.SectionName,
                ServiceProviderIds.ImageGenerationLocalSdHttp,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceEditorStateDto(
                ServiceId: ImageGenerationOptions.SectionName,
                ActiveProviderId: ServiceProviderIds.ImageGenerationLocalSdHttp,
                Providers: [],
                Readiness: new ServiceEditorReadinessDto("ready", [], [])))
            .Callback(() => routingGate.Activate());

        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(settingsMock.Object),
            new StubHttpClientFactory(handler),
            new Mock<ILlamaRuntimeCoordinator>().Object,
            modeResolver,
            NullLogger<LocalAiStartupWarmupService>.Instance);

        var result = await service.ReconcileLocalServiceAsync(
            ImageGenerationOptions.SectionName,
            requestedModelRef: "flux2-klein-4b-q4ks");

        result.Outcome.Should().Be(LocalServiceReconcileOutcome.Warm);
        settingsMock.VerifyAll();
        selectActiveCalled.Should().BeTrue();
        loadCalled.Should().BeTrue();
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
        private readonly IApplicationSettingsService? _settingsService;

        public ServiceScopeFactoryStub(IApplicationSettingsService? settingsService = null)
        {
            _settingsService = settingsService;
        }

        public IServiceScope CreateScope() => new ServiceScopeStub(_settingsService);
    }

    private sealed class ServiceScopeStub : IServiceScope
    {
        public ServiceScopeStub(IApplicationSettingsService? settingsService)
        {
            var services = new ServiceCollection();
            if (settingsService != null)
            {
                services.AddSingleton(settingsService);
            }

            ServiceProvider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => (ServiceProvider as IDisposable)?.Dispose();
    }

    private sealed class RoutingActivationGate
    {
        public bool Activated { get; private set; }

        public void Activate() => Activated = true;
    }

    private sealed class GatedServiceModeResolver : IServiceModeResolver
    {
        private readonly RoutingActivationGate _gate;
        private readonly string _serviceName;
        private readonly ServiceMode _localMode;

        public GatedServiceModeResolver(RoutingActivationGate gate, string serviceName, ServiceMode localMode)
        {
            _gate = gate;
            _serviceName = serviceName;
            _localMode = localMode;
        }

        public Task<ServiceMode> ResolveAsync(string serviceName, string? modeId, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(serviceName, _serviceName, StringComparison.OrdinalIgnoreCase))
            {
                throw RoutingException.ModeNotFound(serviceName, modeId ?? "default");
            }

            if (!_gate.Activated)
            {
                throw RoutingException.ModeNotFound(serviceName, modeId ?? "default");
            }

            return Task.FromResult(_localMode);
        }

        public Task<IReadOnlyList<ServiceMode>> GetModesAsync(string serviceName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
