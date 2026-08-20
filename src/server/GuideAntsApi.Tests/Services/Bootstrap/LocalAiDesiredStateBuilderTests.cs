using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiDesiredStateBuilderTests
{
    [TestMethod]
    public async Task BuildPlanJsonAsync_EmbeddingsLocalWithModel_WritesEnabledModelPath()
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
            modeResolver);

        var planJson = await builder.BuildPlanJsonAsync();

        planJson.Should().Contain("\"Embeddings\":{\"enabled\":true,\"modelPath\":\"qwen3_embedding_0_6b\"}");
        planJson.Should().Contain("\"SpeechTranscription\":{\"enabled\":false}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_ForceAuxiliaryIdle_DisablesAllAuxiliaryServices()
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
            modeResolver);

        var planJson = await builder.BuildPlanJsonAsync(
            new WarmupDesiredBuildOptions { ForceAuxiliaryIdle = true });

        planJson.Should().Contain("\"Embeddings\":{\"enabled\":false,\"modelPath\":\"qwen3_embedding_0_6b\"}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_ImageGenerationRemoteActive_PreservesDisabledLocalBundle()
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
            modeResolver);

        var planJson = await builder.BuildPlanJsonAsync();

        planJson.Should().Contain(
            "\"ImageGeneration\":{\"enabled\":false,\"bundleId\":\"flux2-klein-4b\"}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_ImageGenerationLocal_UsesServiceModeBundleId()
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
            modeResolver);

        var planJson = await builder.BuildPlanJsonAsync();

        planJson.Should().Contain(
            "\"ImageGeneration\":{\"enabled\":true,\"bundleId\":\"flux2-klein-4b\"}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_ImageGenerationLocalWithoutModelId_Throws()
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
            modeResolver);

        var act = () => builder.BuildPlanJsonAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no model or bundle configured in ServiceModes*");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_ApplicationSettingsChatDefaults_WritesEnabledLlamaRouterAlias()
    {
        const string defaultModelId = "qwen3.6-35b-a3b-mtp-local";
        const string routerAlias = "Qwen3.6-35B-A3B-MTP-GGUF";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"chatdefaults-plan-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        db.Models.Add(new Model
        {
            ModelId = defaultModelId,
            DisplayName = "Qwen 3.6 35B",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{routerAlias}}","runtimeProfileId":"qwen3_6"}""",
        });
        await db.SaveChangesAsync();

        var settings = CreateBundleSettingsService();
        Mock.Get(settings)
            .Setup(s => s.GetSectionAsync("ChatDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsSectionDto(
                "ChatDefaults",
                1,
                "v1",
                DateTime.UtcNow,
                new JsonObject
                {
                    ["DefaultModelId"] = defaultModelId,
                    ["OverrideAllChatModels"] = true,
                },
                new Dictionary<string, bool>()));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(settings, dbOptions),
            new FakeServiceModeResolver());

        var planJson = await builder.BuildPlanJsonAsync();

        planJson.Should().Contain("\"llama\":{\"enabled\":true,\"routerAlias\":\"" + routerAlias + "\"}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_ServiceModesReadFails_DoesNotInventIdlePolicy()
    {
        var configuration = new ConfigurationBuilder().Build();
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        modeResolver
            .Setup(x => x.GetModesAsync(
                RoutedServiceNames.SpeechTranscription,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings unavailable"));
        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(),
            modeResolver.Object);

        var act = () => builder.BuildPlanJsonAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("settings unavailable");
    }

    private static IApplicationSettingsService CreateBundleSettingsService()
    {
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(s => s.GetSectionAsync("ChatDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SettingsSectionDto?)null);
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

    private sealed class ServiceScopeFactoryStub : IServiceScopeFactory
    {
        private readonly IApplicationSettingsService? _settingsService;
        private readonly DbContextOptions<ApplicationDbContext>? _dbOptions;

        public ServiceScopeFactoryStub(
            IApplicationSettingsService? settingsService = null,
            DbContextOptions<ApplicationDbContext>? dbOptions = null)
        {
            _settingsService = settingsService;
            _dbOptions = dbOptions;
        }

        public IServiceScope CreateScope() => new ServiceScopeStub(_settingsService, _dbOptions);
    }

    private sealed class ServiceScopeStub : IServiceScope
    {
        public ServiceScopeStub(
            IApplicationSettingsService? settingsService,
            DbContextOptions<ApplicationDbContext>? dbOptions = null)
        {
            var services = new ServiceCollection();
            if (settingsService is not null)
            {
                services.AddSingleton(settingsService);
            }

            if (dbOptions is not null)
            {
                services.AddSingleton(dbOptions);
                services.AddScoped<ApplicationDbContext>();
            }

            ServiceProvider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => (ServiceProvider as IDisposable)?.Dispose();
    }
}
