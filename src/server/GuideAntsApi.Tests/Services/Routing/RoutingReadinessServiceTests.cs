using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class RoutingReadinessServiceTests
{
    [TestMethod]
    public async Task ProbeModeAsync_ReturnsReady_WhenAllComponentsAreReady()
    {
        using var db = CreateDb();
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "AzureOpenAiEmbedding",
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        // Legacy single "default" cloud rows normalize to mode id "cloud" (+ local sibling).
        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "cloud");

        result.Status.Should().Be("ready");
        result.Blockers.Should().BeEmpty();
        result.ProviderSection.Should().Be("AzureOpenAiEmbedding");
        result.ModelId.Should().BeNull();
        result.RuntimeState.Should().BeNull();
    }

    [TestMethod]
    public async Task ProbeModeAsync_ReturnsBlocked_WhenProviderSectionMissingRequiredFields()
    {
        using var db = CreateDb();
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "AzureOpenAiEmbedding",
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults(overrides: new Dictionary<string, string?>
        {
            ["AzureOpenAiEmbedding:ApiKey"] = null
        });
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "cloud");

        result.Status.Should().Be("blocked");
        result.Blockers.Should().ContainSingle(b => b.Contains("AzureOpenAiEmbedding:ApiKey", StringComparison.Ordinal));
        result.Blockers.Should().OnlyContain(b => b.StartsWith(RoutingReadinessService.BlockerKeys.ProviderMissing, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProbeModeAsync_ReturnsBlocked_WhenBoundModelIsMissingFromCatalog()
    {
        using var db = CreateDb();
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "AzureOpenAiEmbedding",
            ModelId: "missing-model",
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "cloud");

        result.Status.Should().Be("blocked");
        result.Blockers.Should().Contain(b => b.StartsWith(RoutingReadinessService.BlockerKeys.ModelMissing, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProbeModeAsync_ReturnsBlocked_WhenBoundModelIsInactive()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "cool-model",
            DisplayName = "Cool Model",
            Provider = "openai-chat",
            IsActive = false,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "AzureOpenAiEmbedding",
            ModelId: "cool-model",
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "cloud");

        result.Status.Should().Be("blocked");
        result.Blockers.Should().Contain(b => b.StartsWith(RoutingReadinessService.BlockerKeys.ModelInactive, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProbeModeAsync_LlamaMode_Unloaded_ReturnsBlockerWithRuntimeStateKey()
    {
        using var db = CreateDb();

        const string routerAlias = "qwen-router";
        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            LocalRuntimeJson = $"{{\"routerModelId\":\"{routerAlias}\",\"resourceGroupKey\":\"rg\",\"runtimeProfileId\":\"qwen3_5\"}}",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "llama-default",
            ProviderSection: "LlamaCpp",
            ModelId: "qwen-local",
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new LlamaRuntimeInventoryItemDto(
                    RouterModelId: routerAlias,
                    RuntimeState: "unloaded",
                    ModelPath: "/models-local/llama/qwen.gguf",
                    MmprojPath: null,
                    HasModelFile: true,
                    HasMmprojFile: false,
                    CatalogModelIds: new[] { "qwen-local" },
                    NotebookReferenceCount: 0)
            });

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "llama-default");

        result.Status.Should().Be("blocked");
        result.RuntimeState.Should().Be("unloaded");
        result.Blockers.Should().Contain(b => b.StartsWith(RoutingReadinessService.BlockerKeys.RuntimeState, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProbeChatTargetAsync_ReturnsUsageCount_OfActiveAssistants()
    {
        using var db = CreateDb();

        db.Models.Add(new Model
        {
            ModelId = "gpt-4",
            DisplayName = "GPT-4",
            Provider = "azure-openai-chat",
            IsActive = true,
            Created = DateTime.UtcNow
        });

        db.Assistants.AddRange(
            new Assistant { Id = Guid.NewGuid(), Name = "A", ModelId = "gpt-4", IsActive = true, Kind = AssistantKind.Assistant },
            new Assistant { Id = Guid.NewGuid(), Name = "B", ModelId = "gpt-4", IsActive = true, Kind = AssistantKind.Assistant },
            new Assistant { Id = Guid.NewGuid(), Name = "Inactive", ModelId = "gpt-4", IsActive = false, Kind = AssistantKind.Assistant },
            new Assistant { Id = Guid.NewGuid(), Name = "Different", ModelId = "other-model", IsActive = true, Kind = AssistantKind.Assistant });
        db.SaveChanges();

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeChatTargetAsync("gpt-4");

        result.AssistantUsageCount.Should().Be(2);
        result.Provider.Should().Be("azure-openai-chat");
        result.ReferenceKind.Should().Be("direct");
    }

    [TestMethod]
    public async Task ProbeChatTargetAsync_ReferenceKind_IsAlwaysDirect()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "claude-3",
            DisplayName = "Claude 3",
            Provider = "anthropic",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        var configuration = BuildConfigurationWithDefaults(overrides: new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = "k"
        });
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var ready = await service.ProbeChatTargetAsync("claude-3");
        ready.ReferenceKind.Should().Be("direct");

        // Also holds when blocked.
        var blocked = await service.ProbeChatTargetAsync("nonexistent");
        blocked.ReferenceKind.Should().Be("direct");
        blocked.Status.Should().Be("blocked");
    }

    // Phase F (R-6.* / R-4.*): per-model readiness probe backing
    // GET /api/settings/routing/chat-targets/{modelId}/readiness. The endpoint
    // maps ModelMissing → 404 and (in strict mode) any other blocker → 409.
    // These tests assert the BlockerKeys shape the endpoint branches on so the
    // HTTP contract is stable without needing a web host.

    [TestMethod]
    public async Task ProbeChatTargetAsync_ReturnsReady_ForConfiguredChatModel()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "claude-3",
            DisplayName = "Claude 3",
            Provider = "anthropic",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        var configuration = BuildConfigurationWithDefaults(overrides: new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = "k"
        });
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeChatTargetAsync("claude-3");

        result.Status.Should().Be("ready");
        result.Blockers.Should().BeEmpty();
        result.ModelId.Should().Be("claude-3");
    }

    [TestMethod]
    public async Task ProbeChatTargetAsync_SurfacesModelMissingBlockerKey_ForUnknownModel()
    {
        using var db = CreateDb();
        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeChatTargetAsync("does-not-exist");

        result.Status.Should().Be("blocked");
        result.Blockers.Should().ContainSingle(b =>
            b.StartsWith(RoutingReadinessService.BlockerKeys.ModelMissing + ":", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProbeChatTargetAsync_SurfacesProviderBlockerKey_WhenConnectionIncomplete()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "claude-3",
            DisplayName = "Claude 3",
            Provider = "anthropic",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        // Explicitly clear Anthropic:ApiKey (the defaults set it) so that the
        // provider readiness probe emits a ProviderMissing blocker. The endpoint
        // maps this to 409 (not 404) in strict mode.
        var configuration = BuildConfigurationWithDefaults(overrides: new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = null
        });
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeChatTargetAsync("claude-3");

        result.Status.Should().Be("blocked");
        result.Blockers.Should().NotContain(b =>
            b.StartsWith(RoutingReadinessService.BlockerKeys.ModelMissing + ":", StringComparison.Ordinal));
        result.Blockers.Should().Contain(b =>
            b.StartsWith(RoutingReadinessService.BlockerKeys.ProviderMissing + ":", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("openai-chat", "OpenAI")]
    [DataRow("openai-responses", "OpenAI")]
    [DataRow("azure-openai-chat", "AzureOpenAI")]
    [DataRow("azure-openai-responses", "AzureOpenAI")]
    [DataRow("anthropic", "Anthropic")]
    [DataRow("llama-cpp", "LlamaCpp")]
    public void MapChatProviderToSection_CoversEveryProvider_KnownToChatTargetValidator(string provider, string expectedSection)
    {
        // Regression guard: MapChatProviderToSection must recognize every
        // catalog provider string that IChatTargetValidator.KnownProviders
        // accepts. Any provider the validator accepts but this mapper does
        // not will surface a spurious PROVIDER_MISSING_FIELDS blocker on a
        // working chat model. The Azure-prefixed strings were added when the
        // OpenAI-platform / Azure-OpenAI split was untangled — before that,
        // "openai-chat" was used for both platforms and readiness probed the
        // wrong section.
        RoutingReadinessService
            .MapChatProviderToSection(provider)
            .Should().Be(expectedSection);
    }

    [TestMethod]
    [DataRow("openai")]
    [DataRow("azure-openai")]
    [DataRow("gpt")]
    [DataRow("")]
    public void MapChatProviderToSection_RejectsLegacyAndUnknownProviderStrings(string provider)
    {
        // The pre-split bare "openai" / "azure-openai" aliases were
        // canonicalized to the suffixed variants by the
        // RenameOpenAiChatProvidersToAzure migration and are no longer a
        // valid input to the readiness mapper. Any stray row carrying one
        // of these values indicates a data-migration hole and must fail
        // open (null) so the overview surfaces an unrecognized-provider
        // blocker rather than silently routing credentials to the wrong
        // section.
        RoutingReadinessService
            .MapChatProviderToSection(provider)
            .Should().BeNull();
    }

    [TestMethod]
    public async Task ProbeChatTargetAsync_OpenAiAndAzureVariants_AreNotFlaggedAsUnrecognized()
    {
        // End-to-end version of the MapChatProviderToSection regression guard:
        // catalog rows tagged with each of the four recognized OpenAI-family
        // provider strings must probe ready against the defaults-config
        // scaffold, not blocked. Before the split was untangled, the readiness
        // probe mapped azure-backed rows to the OpenAI section and emitted
        // PROVIDER_MISSING_FIELDS for OpenAI:ApiKey — even though the chat
        // dispatch path was happily serving traffic via Azure credentials.
        foreach (var provider in new[] { "openai-chat", "openai-responses", "azure-openai-chat", "azure-openai-responses" })
        {
            using var db = CreateDb();
            db.Models.Add(new Model
            {
                ModelId = $"gpt-via-{provider}",
                DisplayName = provider,
                Provider = provider,
                IsActive = true,
                Created = DateTime.UtcNow
            });
            db.SaveChanges();

            var configuration = BuildConfigurationWithDefaults();
            var appSettings = CreateAppSettings(db, configuration);
            var inventory = new Mock<ILlamaRuntimeInventoryService>();
            inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

            var service = new RoutingReadinessService(
                appSettings,
                inventory.Object,
                new TestServiceScopeFactory(db, configuration),
                Mock.Of<ILogger<RoutingReadinessService>>());

            var result = await service.ProbeChatTargetAsync($"gpt-via-{provider}");

            result.Status.Should().Be(
                "ready",
                $"provider '{provider}' must map to its section and see the configured fields");
            result.Blockers.Should().NotContain(
                b => b.Contains("is not a recognized chat provider", StringComparison.Ordinal),
                $"provider '{provider}' must not be flagged as unrecognized");
        }
    }

    [TestMethod]
    public async Task ProbeModeAsync_LlamaMode_DoesNotBlockOnRuntimeArtifactMissing_WhenRouterDeclaresPath_AndRuntimeLoaded()
    {
        // Fix for host-filesystem probe false-positives: when the runtime lives
        // in a named Docker volume the API process cannot see, File.Exists on
        // the ModelStorePath is always false. RUNTIME_ARTIFACT_MISSING must
        // fire only when there is no evidence anywhere that an artifact exists
        // — i.e. router-models.ini has no declaration AND llama-server does
        // not report the alias. Router path present + runtime loaded is a
        // healthy state regardless of HasModelFile.
        using var db = CreateDb();
        const string routerAlias = "qwen-router";

        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            LocalRuntimeJson = $"{{\"routerModelId\":\"{routerAlias}\",\"resourceGroupKey\":\"rg\",\"runtimeProfileId\":\"qwen3_5\"}}",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "llama-default",
            ProviderSection: "LlamaCpp",
            ModelId: "qwen-local",
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new LlamaRuntimeInventoryItemDto(
                    RouterModelId: routerAlias,
                    RuntimeState: "loaded",
                    ModelPath: "/models-local/llama/qwen.gguf",
                    MmprojPath: null,
                    HasModelFile: false,
                    HasMmprojFile: false,
                    CatalogModelIds: new[] { "qwen-local" },
                    NotebookReferenceCount: 0)
            });

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "llama-default");

        result.Status.Should().Be("ready");
        result.RuntimeState.Should().Be("loaded");
        result.Blockers.Should().NotContain(
            b => b.StartsWith(RoutingReadinessService.BlockerKeys.RuntimeArtifactMissing, StringComparison.Ordinal),
            "HasModelFile=false is not authoritative when the router declares a path and the runtime reports the alias as loaded");
    }

    [TestMethod]
    public async Task ProbeModeAsync_LlamaMode_DoesNotBlockOnRuntimeArtifactMissing_WhenRuntimeLoaded_EvenWithoutRouterPath()
    {
        // Runtime truth dominates: if llama-server says the alias is loaded,
        // the artifact is necessarily accessible to it — RUNTIME_ARTIFACT_MISSING
        // must not fire even when router-models.ini has no path declaration for
        // this alias. The runtime wouldn't be serving it otherwise.
        using var db = CreateDb();
        const string routerAlias = "qwen-router";

        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            LocalRuntimeJson = $"{{\"routerModelId\":\"{routerAlias}\",\"resourceGroupKey\":\"rg\",\"runtimeProfileId\":\"qwen3_5\"}}",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "llama-default",
            ProviderSection: "LlamaCpp",
            ModelId: "qwen-local",
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new LlamaRuntimeInventoryItemDto(
                    RouterModelId: routerAlias,
                    RuntimeState: "loaded",
                    ModelPath: null,
                    MmprojPath: null,
                    HasModelFile: false,
                    HasMmprojFile: false,
                    CatalogModelIds: new[] { "qwen-local" },
                    NotebookReferenceCount: 0)
            });

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "llama-default");

        result.Blockers.Should().NotContain(
            b => b.StartsWith(RoutingReadinessService.BlockerKeys.RuntimeArtifactMissing, StringComparison.Ordinal),
            "a runtime-loaded alias is definitionally backed by an accessible artifact");
    }

    [TestMethod]
    public async Task ProbeModeAsync_LlamaMode_BlocksOnRuntimeArtifactMissing_WhenNeitherRouterPathNorRuntime()
    {
        // The true "no artifact" state: router-models.ini has no path AND the
        // runtime doesn't report the alias. Both signals absent → fire the
        // blocker. HasModelFile is independent and doesn't influence this.
        using var db = CreateDb();
        const string routerAlias = "qwen-router";

        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            LocalRuntimeJson = $"{{\"routerModelId\":\"{routerAlias}\",\"resourceGroupKey\":\"rg\",\"runtimeProfileId\":\"qwen3_5\"}}",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.SaveChanges();

        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "llama-default",
            ProviderSection: "LlamaCpp",
            ModelId: "qwen-local",
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new LlamaRuntimeInventoryItemDto(
                    RouterModelId: routerAlias,
                    RuntimeState: "unloaded",
                    ModelPath: null,
                    MmprojPath: null,
                    HasModelFile: false,
                    HasMmprojFile: false,
                    CatalogModelIds: new[] { "qwen-local" },
                    NotebookReferenceCount: 0)
            });

        var service = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "llama-default");

        result.Status.Should().Be("blocked");
        result.Blockers.Should().Contain(
            b => b.StartsWith(RoutingReadinessService.BlockerKeys.RuntimeArtifactMissing, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Overview_Shape_HasOneRollupPerRoutedService()
    {
        // Lightweight integration-ish assembly: exercise the same composition the
        // /overview endpoint performs. We don't boot the full web host — we call
        // the collaborating services directly to prove the shape.
        using var db = CreateDb();

        foreach (var serviceName in RoutedServiceNames.All)
        {
            var section = serviceName switch
            {
                RoutedServiceNames.Embeddings => "AzureOpenAiEmbedding",
                RoutedServiceNames.ImageGeneration => "AzureOpenAiImages",
                RoutedServiceNames.SpeechTranscription => "AzureSpeechService",
                RoutedServiceNames.SpeechSynthesis => "AzureSpeechService",
                RoutedServiceNames.DocumentIntelligence => "AzureDocumentIntelligence",
                _ => throw new InvalidOperationException()
            };
            SeedMode(db, serviceName, new ServiceMode(
                ModeId: "default",
                ProviderSection: section,
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true));
        }

        db.Models.Add(new Model
        {
            ModelId = "gpt-4",
            DisplayName = "GPT-4",
            Provider = "azure-openai-chat",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.Assistants.Add(new Assistant
        {
            Id = Guid.NewGuid(),
            Name = "Helper",
            ModelId = "gpt-4",
            IsActive = true,
            Kind = AssistantKind.Assistant
        });
        db.SaveChanges();

        var configuration = BuildConfigurationWithDefaults();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var readinessService = new RoutingReadinessService(
            appSettings,
            inventory.Object,
            new TestServiceScopeFactory(db, configuration),
            Mock.Of<ILogger<RoutingReadinessService>>());

        var rollups = new List<ServiceModeRollupDto>();
        foreach (var serviceName in RoutedServiceNames.All)
        {
            var modes = await appSettings.GetServiceModesAsync(serviceName);
            var probed = new List<ModeReadinessDto>();
            foreach (var m in modes)
            {
                probed.Add(await readinessService.ProbeModeAsync(serviceName, m.ModeId));
            }
            rollups.Add(new ServiceModeRollupDto(
                serviceName,
                probed.Count(x => x.Status == "ready"),
                probed.Count,
                probed));
        }

        var chatTargetIds = await db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId != null)
            .Select(a => a.ModelId!)
            .Distinct()
            .ToListAsync();
        var targets = new List<ChatTargetReadinessDto>();
        foreach (var id in chatTargetIds)
        {
            targets.Add(await readinessService.ProbeChatTargetAsync(id));
        }

        var overview = new SettingsOverviewDto(
            GeneratedUtc: DateTime.UtcNow,
            ServiceModeReadiness: rollups,
            ChatTargets: new ChatTargetsRollupDto(
                targets.Count(x => x.Status == "ready"),
                targets.Count,
                targets),
            ProviderIssues: Array.Empty<ProviderConnectionIssueDto>(),
            LlamaRuntime: new LlamaRuntimeSnapshotDto(0, 0, Array.Empty<string>()));

        overview.ServiceModeReadiness.Should().HaveCount(RoutedServiceNames.All.Count);
        // Legacy single "default" rows normalize to cloud + local modes per service.
        overview.ServiceModeReadiness.Should().OnlyContain(r => r.Total == 2);
        overview.ServiceModeReadiness.Should().OnlyContain(r => r.Ready == 2);
        overview.ChatTargets.Total.Should().Be(1);
        overview.ChatTargets.Targets.Should().OnlyContain(t => t.ReferenceKind == "direct");
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"routing-readiness-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static void SeedMode(ApplicationDbContext db, string serviceName, ServiceMode mode)
    {
        var row = db.ApplicationSettings.SingleOrDefault(x => x.SectionName == ServiceModeResolver.SectionName);
        JsonObject payload;
        if (row == null)
        {
            payload = new JsonObject();
            ServiceModesPayload.WriteModesFor(payload, serviceName, new[] { mode }, mode.ModeId);
            db.ApplicationSettings.Add(new ApplicationSetting
            {
                SectionName = ServiceModeResolver.SectionName,
                SchemaVersion = 1,
                JsonValue = payload.ToJsonString(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            payload = JsonNode.Parse(row.JsonValue) as JsonObject ?? new JsonObject();
            var existing = ServiceModesPayload.ReadModesFor(payload, serviceName).ToList();
            existing.RemoveAll(m => m.ModeId == mode.ModeId);
            existing.Add(mode);
            ServiceModesPayload.WriteModesFor(
                payload,
                serviceName,
                existing,
                existing.FirstOrDefault(m => m.IsDefault)?.ModeId ?? existing.FirstOrDefault()?.ModeId);
            row.JsonValue = payload.ToJsonString();
        }

        db.SaveChanges();
    }

    private static IConfiguration BuildConfigurationWithDefaults(IDictionary<string, string?>? overrides = null)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = "tests",
            ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
            ["Ui:RootPath"] = "./ui",
            ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp",
            ["ServiceRouting:Containers:guideants-ai:BaseUrl"] = "http://localhost:8110/sandbox",

            ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:MediaBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:DocumentIntelligenceBaseUrl"] = "http://localhost:5001",

            ["AzureSpeechService:Endpoint"] = "https://speech.example.com/",
            ["AzureSpeechService:ApiKey"] = "test-speech-key",
            ["AzureSpeechService:Region"] = "eastus2",

            ["AzureOpenAiEmbedding:Endpoint"] = "https://embedding-api.example.com/",
            ["AzureOpenAiEmbedding:ApiKey"] = "test-embedding-key",
            ["AzureOpenAiEmbedding:Deployment"] = "text-embedding-3-small",

            ["AzureOpenAiImages:Endpoint"] = "https://image-api.example.com/",
            ["AzureOpenAiImages:ApiKey"] = "test-api-key",
            ["AzureOpenAiImages:Deployment"] = "flux-1",
            ["AzureOpenAiImages:EditModelDeployment"] = "flux-1-edit",

            ["AzureDocumentIntelligence:Endpoint"] = "https://doc-intel.example.com/",
            ["AzureDocumentIntelligence:ApiKey"] = "test-doc-intel-key",

            ["AzureOpenAI:Resource"] = "test-resource",
            ["AzureOpenAI:ApiKey"] = "test-aoai-key",
            ["AzureOpenAI:Deployment"] = "gpt-4",

            ["OpenAI:ApiKey"] = "test-openai-key",

            ["Anthropic:ApiKey"] = "test-anthropic-key"
        };

        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                defaults[pair.Key] = pair.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults!)
            .Build();
    }

    private static ApplicationSettingsService CreateAppSettings(ApplicationDbContext db, IConfiguration configuration)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(v => v.ContentRootPath).Returns(AppContext.BaseDirectory);

        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(v => v.CurrentValue).Returns(new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            }
        });

        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();

        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object,
            runtimeProfileResolver.Object);
    }
}
