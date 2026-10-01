using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalServiceLoadServiceTests
{
    [TestMethod]
    public async Task LoadServiceAsync_PostsModelPath_ForAsr()
    {
        var handler = new CapturingHandler(_ => ReadyJson(loaded: true));
        var (service, _) = CreateService(handler, local: true, modelRef: "qwen-asr");

        var result = await service.LoadServiceAsync(RoutedServiceNames.SpeechTranscription, "qwen-asr");

        result.Success.Should().BeTrue();
        var load = handler.Requests.Single(r => r.AbsolutePath.EndsWith("/admin/load"));
        load.Method.Should().Be(HttpMethod.Post);
        var body = load.Body!;
        body.Should().Contain("\"model_path\":\"qwen-asr\"");
    }

    [TestMethod]
    public async Task LoadServiceAsync_PostsBundleId_ForImageGeneration()
    {
        var handler = new CapturingHandler(_ => HealthJson(status: "ok", bundleId: "flux"));
        var (service, _) = CreateService(handler, local: true, modelRef: "flux");

        var result = await service.LoadServiceAsync(RoutedServiceNames.ImageGeneration, "flux");

        result.Success.Should().BeTrue();
        var load = handler.Requests.Single(r => r.AbsolutePath.EndsWith("/admin/load"));
        var body = load.Body!;
        body.Should().Contain("\"bundle_id\":\"flux\"");
    }

    [TestMethod]
    public async Task LoadServiceAsync_ReturnsFailure_WhenBackendReturnsError()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"bundle not found\"}", Encoding.UTF8, "application/json"),
            });
        var (service, _) = CreateService(handler, local: true, modelRef: "flux");

        var result = await service.LoadServiceAsync(RoutedServiceNames.ImageGeneration, "flux");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("bundle not found");
    }

    [TestMethod]
    public async Task UnloadServiceAsync_PostsUnload()
    {
        var handler = new CapturingHandler(_ => ReadyJson(loaded: false));
        var (service, _) = CreateService(handler, local: true, modelRef: "qwen-asr");

        var result = await service.UnloadServiceAsync(RoutedServiceNames.SpeechTranscription);

        result.Success.Should().BeTrue();
        handler.Requests.Should().Contain(r =>
            r.AbsolutePath.EndsWith("/admin/unload") && r.Method == HttpMethod.Post);
    }

    [TestMethod]
    public async Task EnsureLoadedAsync_LoadsWhenNotLoadedAndModelRefPersisted()
    {
        // First probe: not loaded. After load: loaded.
        var probes = 0;
        var handler = new CapturingHandler(_ =>
        {
            probes++;
            return probes > 1 ? ReadyJson(loaded: true) : ReadyJson(loaded: false);
        });
        var (service, _) = CreateService(handler, local: true, modelRef: "persisted");

        var result = await service.EnsureLoadedAsync(RoutedServiceNames.Embeddings);

        result.Success.Should().BeTrue();
        handler.Requests.Should().Contain(r => r.AbsolutePath.EndsWith("/admin/load"));
    }

    [TestMethod]
    public async Task EnsureLoadedAsync_SkipsWhenNoModelRefPersisted()
    {
        var handler = new CapturingHandler(_ => ReadyJson(loaded: false));
        var (service, _) = CreateService(handler, local: true, modelRef: null);

        var result = await service.EnsureLoadedAsync(RoutedServiceNames.Embeddings);

        result.Success.Should().BeTrue();
        result.Error.Should().Contain("No local model is selected");
        handler.Requests.Should().OnlyContain(r => r.AbsolutePath.EndsWith("/ready"));
    }

    [TestMethod]
    public async Task EnsureLoadedAsync_IsIndependentPerService()
    {
        // ASR is not loaded and has a ref -> it loads. TTS is not loaded and has no ref
        // -> it skips. Neither operation touches the other service.
        var asrHandler = new CapturingHandler(_ => ReadyJson(loaded: true));
        var (service, _) = CreateService(asrHandler, local: true, modelRef: "asr-model");

        var asrResult = await service.EnsureLoadedAsync(RoutedServiceNames.SpeechTranscription);

        asrResult.Success.Should().BeTrue();
        asrHandler.Requests.Should().OnlyContain(r =>
            r.AbsolutePath.Contains("/asr/") || r.AbsolutePath.EndsWith("/ready")
            || r.AbsolutePath.EndsWith("/admin/load"));
    }

    [TestMethod]
    public async Task StartupEnsureAsync_FailureInOneServiceDoesNotStopOthers()
    {
        // TTS admin base is unreachable (connection refused); the others are healthy and
        // loaded. Startup must record the TTS failure and still process the rest.
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/tts/"))
            {
                throw new System.Net.Http.HttpRequestException("connection refused");
            }

            return ReadyJson(loaded: true);
        });
        var (service, _) = CreateService(handler, local: true, modelRef: "model");

        var results = await service.StartupEnsureAsync();

        results.Should().HaveCount(4);
        results.Single(r => r.ServiceId == RoutedServiceNames.SpeechSynthesis).Success.Should().BeFalse();
        results.Single(r => r.ServiceId == RoutedServiceNames.SpeechTranscription).Success.Should().BeTrue();
        results.Single(r => r.ServiceId == RoutedServiceNames.Embeddings).Success.Should().BeTrue();
        results.Single(r => r.ServiceId == RoutedServiceNames.ImageGeneration).Success.Should().BeTrue();
    }

    [TestMethod]
    public async Task StartupEnsureAsync_SkipsServicesWhoseActiveProviderIsNotLocal()
    {
        var handler = new CapturingHandler(_ => ReadyJson(loaded: true));
        // Only SpeechTranscription is local; the rest route to cloud.
        var (service, _) = CreateService(handler, local: true, modelRef: "model", localServices:
        [
            RoutedServiceNames.SpeechTranscription,
        ]);

        var results = await service.StartupEnsureAsync();

        results.Single(r => r.ServiceId == RoutedServiceNames.SpeechTranscription).Success.Should().BeTrue();
        results.Single(r => r.ServiceId == RoutedServiceNames.SpeechSynthesis).Error.Should().Contain("not local");
        handler.Requests.Should().OnlyContain(r =>
            r.AbsolutePath.Contains("/asr/") || r.AbsolutePath.EndsWith("/ready"));
    }

    // --- fixtures ---

    private static (LocalServiceLoadService Service, CapturingHandler Handler) CreateService(
        CapturingHandler handler,
        bool local,
        string? modelRef,
        string[]? localServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110/asr",
                ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110/tts",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110/emb",
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110/sd",
            })
            .Build();

        var localSections = (localServices ?? new[]
        {
            RoutedServiceNames.SpeechTranscription,
            RoutedServiceNames.SpeechSynthesis,
            RoutedServiceNames.Embeddings,
            RoutedServiceNames.ImageGeneration,
        }).ToHashSet(StringComparer.Ordinal);

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        foreach (var serviceId in new[]
        {
            RoutedServiceNames.SpeechTranscription,
            RoutedServiceNames.SpeechSynthesis,
            RoutedServiceNames.Embeddings,
            RoutedServiceNames.ImageGeneration,
        })
        {
            var section = LocalServiceModeSelectionReader.ResolveLocalProviderSection(serviceId)!;
            var isLocal = local && localSections.Contains(serviceId);
            var modes = isLocal
                ? new[]
                {
                    new ServiceModeDto(
                        serviceId,
                        "local",
                        section,
                        modelRef,
                        null,
                        Enabled: true,
                        IsDefault: true),
                }
                : new[]
                {
                    new ServiceModeDto(
                        serviceId,
                        "cloud",
                        "OpenRouter",
                        "some/cloud-model",
                        null,
                        Enabled: true,
                        IsDefault: true),
                };
            settings
                .Setup(x => x.GetServiceModesAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(modes);
        }

        var factory = new StubHttpClientFactory(handler);
        var scopeFactory = new StubScopeFactory(settings.Object);
        var service = new LocalServiceLoadService(
            factory,
            scopeFactory,
            configuration,
            NullLogger<LocalServiceLoadService>.Instance);

        return (service, handler);
    }

    private static HttpResponseMessage ReadyJson(bool loaded) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ready = loaded,
                    loaded = loaded,
                    loading = false,
                    modelRef = "m",
                }),
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage HealthJson(string status, string? bundleId = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = status,
                    loadedBundleId = bundleId,
                }),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class CapturedRequest
    {
        public required Uri Uri { get; init; }
        public required HttpMethod Method { get; init; }
        public string? Body { get; init; }
        public string AbsolutePath => Uri.AbsolutePath;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add(new CapturedRequest
            {
                Uri = request.RequestUri!,
                Method = request.Method,
                Body = body,
            });
            return _responder(request);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly CapturingHandler _handler;

        public StubHttpClientFactory(CapturingHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler) { BaseAddress = new Uri("http://localhost:1") };
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        private readonly IApplicationSettingsService _settings;

        public StubScopeFactory(IApplicationSettingsService settings)
        {
            _settings = settings;
        }

        public IServiceScope CreateScope() => new StubScope(_settings);
    }

    private sealed class StubScope : IServiceScope, IDisposable
    {
        public StubScope(IApplicationSettingsService settings)
        {
            ServiceProvider = new StubServiceProvider(settings);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
        }
    }

    private sealed class StubServiceProvider : IServiceProvider
    {
        private readonly IApplicationSettingsService _settings;

        public StubServiceProvider(IApplicationSettingsService settings)
        {
            _settings = settings;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IApplicationSettingsService) ? _settings : null;
    }
}
