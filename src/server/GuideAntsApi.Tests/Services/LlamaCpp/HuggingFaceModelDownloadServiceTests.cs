using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public class HuggingFaceModelDownloadServiceTests
{
    [TestMethod]
    public async Task StartDownloadAsync_PersistsCatalogIntent_WhenCatalogFieldsArePresent()
    {
        using var harness = CreateHarness();
        harness.AdminClient
            .Setup(x => x.StartDownloadAsync(
                It.IsAny<StartModelDownloadRequest>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto("op-1", "queued", "router-a", 0, null, "queued"));

        await harness.Service.StartDownloadAsync(CreateCatalogRequest());

        var persisted = await GetIntentAsync(harness.Context, "op-1");
        persisted.Should().NotBeNull();
        persisted!.OperationId.Should().Be("op-1");
        persisted.ModelId.Should().Be("model-a");
        persisted.RouterModelId.Should().Be("router-a");
        persisted.RegisteringCatalogObserved.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_FirstCompletedPoll_ReturnsRegisteringCatalog()
    {
        using var harness = CreateHarness();
        harness.AdminClient
            .Setup(x => x.StartDownloadAsync(
                It.IsAny<StartModelDownloadRequest>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto("op-1", "queued", "router-a", 0, null, "queued"));
        harness.AdminClient
            .Setup(x => x.GetDownloadStatusAsync("op-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto("op-1", "completed", "router-a", 1, null, "done"));

        await harness.Service.StartDownloadAsync(CreateCatalogRequest());
        var firstPoll = await harness.Service.GetOperationStatusAsync("op-1");

        firstPoll.Should().NotBeNull();
        firstPoll!.Status.Should().Be("registeringCatalog");

        var persisted = await GetIntentAsync(harness.Context, "op-1");
        persisted.Should().NotBeNull();
        persisted!.RegisteringCatalogObserved.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_SecondCompletedPoll_RealizesCatalogAndRemovesIntent()
    {
        using var harness = CreateHarness();
        harness.AdminClient
            .Setup(x => x.StartDownloadAsync(
                It.IsAny<StartModelDownloadRequest>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto("op-1", "queued", "router-a", 0, null, "queued"));
        harness.AdminClient
            .Setup(x => x.GetDownloadStatusAsync("op-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto("op-1", "completed", "router-a", 1, null, "done"));

        harness.SettingsService
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        harness.SettingsService
            .Setup(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsModelDto(
                ModelId: "model-a",
                DisplayName: "Model A",
                Provider: "llama-cpp",
                Description: null,
                ReasoningChoicesJson: "[\"enabled\"]",
                LocalRuntimeJson: "{\"routerModelId\":\"router-a\"}",
                IsActive: true,
                DisplayOrder: 1,
                Created: DateTime.UtcNow,
                Updated: null));
        harness.RuntimeProfileResolver
            .Setup(x => x.ResolveAsync("profile-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuntimeProfileData(
                ProfileId: "profile-a",
                CombineSystemAndDeveloperMessages: true,
                ThoughtBlockPattern: null,
                SamplingParameters: new Dictionary<string, SamplingParameterDefinition>(),
                ThinkingControl: new ThinkingControl(
                    "enabled",
                    new Dictionary<string, IReadOnlyList<ThinkingAction>>
                    {
                        ["enabled"] = Array.Empty<ThinkingAction>()
                    })));

        await harness.Service.StartDownloadAsync(CreateCatalogRequest());
        var firstPoll = await harness.Service.GetOperationStatusAsync("op-1");
        var secondPoll = await harness.Service.GetOperationStatusAsync("op-1");

        firstPoll.Should().NotBeNull();
        firstPoll!.Status.Should().Be("registeringCatalog");
        secondPoll.Should().NotBeNull();
        secondPoll!.Status.Should().Be("completed");

        var persisted = await GetIntentAsync(harness.Context, "op-1");
        persisted.Should().BeNull();

        harness.SettingsService.Verify(
            x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_FailedStatus_ClearsIntent()
    {
        using var harness = CreateHarness();
        harness.AdminClient
            .Setup(x => x.StartDownloadAsync(
                It.IsAny<StartModelDownloadRequest>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto("op-1", "queued", "router-a", 0, null, "queued"));
        harness.AdminClient
            .Setup(x => x.GetDownloadStatusAsync("op-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-1",
                Status: "failed",
                RouterModelId: "router-a",
                Progress: 1,
                ErrorMessage: "download failed",
                LogLine: "failed"));

        await harness.Service.StartDownloadAsync(CreateCatalogRequest());
        var poll = await harness.Service.GetOperationStatusAsync("op-1");

        poll.Should().NotBeNull();
        poll!.Status.Should().Be("failed");

        var persisted = await GetIntentAsync(harness.Context, "op-1");
        persisted.Should().BeNull();
    }

    private static StartModelDownloadRequest CreateCatalogRequest()
    {
        return new StartModelDownloadRequest(
            Repository: "repo/model",
            QuantIncludePattern: "*.gguf",
            MmprojIncludePattern: "*mmproj*.gguf",
            RouterModelId: "router-a",
            TargetDirectory: "/models/router-a",
            CatalogModelId: "model-a",
            CatalogDisplayName: "Model A",
            CatalogRuntimeProfileId: "profile-a",
            CatalogDescription: "description",
            CatalogResourceGroupKey: "rg-a",
            CatalogIsActive: true,
            CatalogDisplayOrder: 1,
            CatalogLoadParamsJson: "{\"model\":\"router-a\"}",
            CatalogParallelToolCalls: false);
    }

    private static async Task<LlamaCatalogDownloadIntent?> GetIntentAsync(ApplicationDbContext context, string operationId)
    {
        return await context.LlamaCatalogDownloadIntents
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == operationId);
    }

    private static Harness CreateHarness()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>(MockBehavior.Strict);
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var options = new Mock<IOptionsMonitor<LlamaModelManagementOptions>>(MockBehavior.Strict);

        tokenResolver.Setup(x => x.Resolve()).Returns((string?)null);
        options.SetupGet(x => x.CurrentValue).Returns(new LlamaModelManagementOptions { AllowOverwrite = false });

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"hf-intent-{Guid.NewGuid():N}")
            .Options;
        var context = new ApplicationDbContext(dbOptions);

        var serviceProvider = new Mock<IServiceProvider>(MockBehavior.Strict);
        serviceProvider
            .Setup(x => x.GetService(typeof(ApplicationDbContext)))
            .Returns(context);
        serviceProvider
            .Setup(x => x.GetService(typeof(IApplicationSettingsService)))
            .Returns(settingsService.Object);

        var serviceScope = new Mock<IServiceScope>(MockBehavior.Strict);
        serviceScope
            .SetupGet(x => x.ServiceProvider)
            .Returns(serviceProvider.Object);
        serviceScope
            .Setup(x => x.Dispose());

        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopeFactory
            .Setup(x => x.CreateScope())
            .Returns(serviceScope.Object);

        var service = new HuggingFaceModelDownloadService(
            adminClient.Object,
            tokenResolver.Object,
            runtimeProfileResolver.Object,
            options.Object,
            scopeFactory.Object,
            NullLogger<HuggingFaceModelDownloadService>.Instance);

        return new Harness(
            Service: service,
            Context: context,
            AdminClient: adminClient,
            SettingsService: settingsService,
            RuntimeProfileResolver: runtimeProfileResolver);
    }

    private sealed record Harness(
        HuggingFaceModelDownloadService Service,
        ApplicationDbContext Context,
        Mock<ILlamaRuntimeAdminClient> AdminClient,
        Mock<IApplicationSettingsService> SettingsService,
        Mock<IRuntimeProfileResolver> RuntimeProfileResolver) : IDisposable
    {
        public void Dispose()
        {
            Context.Database.EnsureDeleted();
            Context.Dispose();
        }
    }
}
