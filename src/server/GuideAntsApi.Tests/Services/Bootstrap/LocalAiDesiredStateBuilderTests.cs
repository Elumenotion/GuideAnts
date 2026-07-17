using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiDesiredStateBuilderTests
{
    [TestMethod]
    public async Task BuildIniAsync_EmbeddingsLocalWithModel_WritesWarmAndModelPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.Embeddings, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                ModelId: "qwen3_embedding_0_6b",
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

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService()),
            modeResolver,
            new StubHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var ini = await builder.BuildIniAsync();

        ini.Should().Contain("[Embeddings]");
        ini.Should().Contain("model_path = qwen3_embedding_0_6b");
        ini.Should().NotContain("desired = warm");
        ini.Should().Contain("[SpeechTranscription]");
        ini.Should().Contain("enabled = off");
    }

    [TestMethod]
    public async Task BuildIniAsync_ForceAuxiliaryIdle_WritesAllAuxIdle()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.Embeddings, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                ModelId: "qwen3_embedding_0_6b",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService()),
            modeResolver,
            new StubHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var ini = await builder.BuildIniAsync(new WarmupDesiredBuildOptions { ForceAuxiliaryIdle = true });

        ini.Should().Contain("[Embeddings]");
        ini.Should().Contain("enabled = off");
        ini.Should().Contain("model_path = qwen3_embedding_0_6b");
    }

    [TestMethod]
    public async Task BuildIniAsync_ImageGenerationRemoteActive_PreservesLocalBundleIdAsOff()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.ImageGeneration, new ServiceMode(
                ModeId: "ImageGeneration.OpenRouter.Image",
                ProviderSection: "OpenRouter",
                ModelId: "recraft/recraft-v4",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)),
            (RoutedServiceNames.ImageGeneration, new ServiceMode(
                ModeId: "ImageGeneration.LocalSd.Http",
                ProviderSection: "LocalServiceHosts:ImageGenerationBaseUrl",
                ModelId: "flux2-klein-4b",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: false)));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService()),
            modeResolver,
            new StubHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var ini = await builder.BuildIniAsync();

        ini.Should().Contain("[ImageGeneration]");
        ini.Should().Contain("enabled = off");
        ini.Should().Contain("bundle_id = flux2-klein-4b");
    }

    [TestMethod]
    public async Task BuildIniAsync_ImageGenerationLocal_UsesPersistedServiceModeBundleId()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.ImageGeneration, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:ImageGenerationBaseUrl",
                ModelId: "flux2-klein-4b",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService()),
            modeResolver,
            new StubHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var ini = await builder.BuildIniAsync();

        ini.Should().Contain("[ImageGeneration]");
        ini.Should().Contain("bundle_id = flux2-klein-4b");
        ini.Should().NotContain("desired = warm");
    }

    [TestMethod]
    public async Task BuildIniAsync_ImageGenerationLocalWithoutModelId_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.ImageGeneration, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:ImageGenerationBaseUrl",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService()),
            modeResolver,
            new StubHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var act = () => builder.BuildIniAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no model or bundle configured in ServiceModes*");
    }

    private static IApplicationSettingsService CreateBundleSettingsService()
    {
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(s => s.GetImageGenerationBundleDefinitionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string bundleId, CancellationToken _) => new ImageGenerationBundleDefinitionDto(
                bundleId,
                null,
                null,
                new BundleDefinitionRolesDto(
                    new BundleDefinitionRoleDto("org/diff", "model.gguf"),
                    new BundleDefinitionRoleDto("org/vae", "vae.safetensors"),
                    new BundleDefinitionRoleDto("org/te", "te.gguf")),
                new BundleDefinitionSamplingDto(4, 1.0, "euler")));
        return settings.Object;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses;

        public StubHttpClientFactory(Dictionary<string, HttpResponseMessage>? responses = null)
        {
            _responses = responses ?? new Dictionary<string, HttpResponseMessage>(StringComparer.OrdinalIgnoreCase);
        }

        public HttpClient CreateClient(string name) =>
            new(new StubHttpMessageHandler(_responses));

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, HttpResponseMessage> _responses;

            public StubHttpMessageHandler(Dictionary<string, HttpResponseMessage> responses) =>
                _responses = responses;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri?.ToString() ?? string.Empty;
                foreach (var (key, response) in _responses)
                {
                    if (uri.Contains(key, StringComparison.OrdinalIgnoreCase)
                        || key.Contains(uri, StringComparison.OrdinalIgnoreCase)
                        || uri.TrimEnd('/').EndsWith("/sd/admin/bundles", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(CloneResponse(response));
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage CloneResponse(HttpResponseMessage template)
            {
                var clone = new HttpResponseMessage(template.StatusCode);
                if (template.Content is not null)
                {
                    var body = template.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                return clone;
            }
        }
    }

    private sealed class ServiceScopeFactoryStub : IServiceScopeFactory
    {
        private readonly IApplicationSettingsService? _settingsService;

        public ServiceScopeFactoryStub(IApplicationSettingsService? settingsService = null) =>
            _settingsService = settingsService;

        public IServiceScope CreateScope() => new ServiceScopeStub(_settingsService);
    }

    private sealed class ServiceScopeStub : IServiceScope
    {
        public ServiceScopeStub(IApplicationSettingsService? settingsService)
        {
            var services = new ServiceCollection();
            if (settingsService is not null)
            {
                services.AddSingleton(settingsService);
            }

            ServiceProvider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => (ServiceProvider as IDisposable)?.Dispose();
    }
}
