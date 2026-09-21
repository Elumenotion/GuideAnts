using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
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
            modeResolver,
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

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
            modeResolver,
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

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
            modeResolver,
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

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
            modeResolver,
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

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
            modeResolver,
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

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
            new FakeServiceModeResolver(),
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

        var planJson = await builder.BuildPlanJsonAsync();

        // Per-instance plan: the section is keyed by the canonical instance key (the
        // default machine resolves to a literal IP, so localhost:8080 -> 127.0.0.1:8080)
        // and carries the default's alias.
        planJson.Should().Contain("\"llama.127.0.0.1:8080\":{\"enabled\":true,\"routerAlias\":\"" + routerAlias + "\"}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_RowOwnedInstance_EmitsSectionPerInstance()
    {
        const string defaultModelId = "qwen3.6-35b-a3b-mtp-local";
        const string defaultAlias = "Qwen3.6-35B-A3B-MTP-GGUF";
        const string maxModelId = "qwen3.6-35b-a3b-mtp-max";
        const string maxAlias = "Qwen3.6-35B-A3B-MTP-GGUF-Max";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"perinstance-plan-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        db.Models.Add(new Model
        {
            ModelId = defaultModelId,
            DisplayName = "Qwen 3.6 35B local",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{defaultAlias}}","runtimeProfileId":"qwen3_6"}""",
        });
        db.Models.Add(new Model
        {
            ModelId = maxModelId,
            DisplayName = "Qwen 3.6 35B max",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{maxAlias}}","stackBaseUrl":"http://192.0.2.1:8112"}""",
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
            new FakeServiceModeResolver(),
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration, new ServiceScopeFactoryStub(settings, dbOptions)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var planJson = await builder.BuildPlanJsonAsync();

        // The default model's machine (127.0.0.1:8080) carries the default's alias. A row
        // that declares placement of maxAlias on 192.0.2.1:8112 is not a load demand:
        // with nothing asking for maxAlias, that instance is disabled, not pre-loaded.
        planJson.Should().Contain("\"llama.127.0.0.1:8080\":{\"enabled\":true,\"routerAlias\":\"" + defaultAlias + "\"}");
        planJson.Should().Contain("\"llama.192.0.2.1:8112\":{\"enabled\":false}");
        planJson.Should().NotContain(maxAlias);
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_NotebookAliasKeepsInstanceEnabled_OthersDisabled()
    {
        // Chat default is cloud (no local default row). The row-owned instance exists in
        // the universe via its row and keeps its alias; the global instance has no
        // default, no row alias, and no notebook alias, so it is disabled - the only
        // unload source, applied via the plan triggers.
        const string maxAlias = "Qwen3.6-35B-A3B-MTP-GGUF-Max";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"notebookplan-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        // The row that owns the row-owned instance (it must exist for the resolver to put
        // the instance in the warmup universe).
        db.Models.Add(new Model
        {
            ModelId = "qwen3.6-35b-a3b-mtp-max",
            DisplayName = "Qwen 3.6 35B max",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{maxAlias}}","stackBaseUrl":"http://192.0.2.1:8112"}""",
        });
        await db.SaveChangesAsync();

        var aliasState = new NotebookChatAliasState();
        aliasState.SetActiveChatAliasForInstance("http://192.0.2.1:8112", maxAlias);

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService(), dbOptions),
            new FakeServiceModeResolver(),
            aliasState,
            new LocalAiStackHostResolver(configuration, new ServiceScopeFactoryStub(CreateBundleSettingsService(), dbOptions)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var planJson = await builder.BuildPlanJsonAsync();

        // The notebook's active alias is live intent: the instance it was set on stays
        // enabled with that alias. The default machine has no alias of its own and is
        // disabled - the only unload source, applied via the plan triggers.
        planJson.Should().Contain("\"llama.192.0.2.1:8112\":{\"enabled\":true,\"routerAlias\":\"" + maxAlias + "\"}");
        planJson.Should().Contain("\"llama.127.0.0.1:8080\":{\"enabled\":false}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_DefaultModelOnSharedRowOwnedInstance_DefaultAliasWins()
    {
        // Regression (2026-09-20 incident): the default model (Muse) and another row
        // (Qwen) both declare the same Max stack. The default -> Muse must be served
        // by Max: the plan for Max carries the default's alias, not the other row's.
        // A placement declaration is not a load demand.
        const string defaultModelId = "muse-glimmer-30b-max";
        const string defaultAlias = "Muse-Glimmer-30B-GGUF";
        const string otherModelId = "qwen3.6-35b-a3b-mtp-max";
        const string otherAlias = "Qwen3.6-35B-A3B-MTP-GGUF";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"sharedmax-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        db.Models.Add(new Model
        {
            ModelId = defaultModelId,
            DisplayName = "Muse Glimmer Max",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{defaultAlias}}","stackBaseUrl":"http://192.0.2.1:8112"}""",
        });
        db.Models.Add(new Model
        {
            ModelId = otherModelId,
            DisplayName = "Qwen 3.6 Max",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{otherAlias}}","stackBaseUrl":"http://192.0.2.1:8112"}""",
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
            new FakeServiceModeResolver(),
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration, new ServiceScopeFactoryStub(settings, dbOptions)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var planJson = await builder.BuildPlanJsonAsync();

        // Max is served by the default model's alias. The other row's alias must not
        // appear anywhere in the plan.
        planJson.Should().Contain("\"llama.192.0.2.1:8112\":{\"enabled\":true,\"routerAlias\":\"" + defaultAlias + "\"}");
        planJson.Should().NotContain(otherAlias);
        // The default machine carries no alias of its own (the default lives on Max).
        planJson.Should().Contain("\"llama.127.0.0.1:8080\":{\"enabled\":false}");
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_LoadOverrideRoutesToTheAliasRowStack()
    {
        // The load path passes the requested alias as an override. It must land on the
        // instance the alias's row declares, never on the default machine.
        const string museAlias = "Muse-Glimmer-30B-GGUF";
        const string qwenLocalAlias = "Qwen3.6-35B-A3B-MTP-GGUF";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"override-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        db.Models.Add(new Model
        {
            ModelId = "qwen3.6-35b-a3b-mtp-local",
            DisplayName = "Qwen 3.6 local",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{qwenLocalAlias}}"}""",
        });
        db.Models.Add(new Model
        {
            ModelId = "muse-glimmer-30b-max",
            DisplayName = "Muse Glimmer Max",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = $$"""{"routerModelId":"{{museAlias}}","stackBaseUrl":"http://192.0.2.1:8112"}""",
        });
        await db.SaveChangesAsync();

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService(), dbOptions),
            new FakeServiceModeResolver(),
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration, new ServiceScopeFactoryStub(CreateBundleSettingsService(), dbOptions)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var planJson = await builder.BuildPlanJsonAsync(
            new WarmupDesiredBuildOptions { LlamaRouterAliasOverride = museAlias });

        // The override (Muse) is addressed to Max, the alias's own stack.
        planJson.Should().Contain("\"llama.192.0.2.1:8112\":{\"enabled\":true,\"routerAlias\":\"" + museAlias + "\"}");
        // The default machine is not told to load a Max-only model.
        planJson.Should().Contain("\"llama.127.0.0.1:8080\":{\"enabled\":false}");
        planJson.Should().NotContain("\"127.0.0.1:8080\":{\"enabled\":true,\"routerAlias\":\"" + museAlias);
    }

    [TestMethod]
    public async Task BuildPlanJsonAsync_NoDefaultNoNotebook_AllLlamaInstancesDisabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"nollama-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        await db.SaveChangesAsync();

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(CreateBundleSettingsService(), dbOptions),
            new FakeServiceModeResolver(),
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration, new ServiceScopeFactoryStub(CreateBundleSettingsService(), dbOptions)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var planJson = await builder.BuildPlanJsonAsync();

        planJson.Should().Contain("\"llama.127.0.0.1:8080\":{\"enabled\":false}");
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
            modeResolver.Object,
            new NotebookChatAliasState(),
            new LocalAiStackHostResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideAntsApi.Services.Bootstrap.LocalAiDesiredStateBuilder>.Instance);

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
