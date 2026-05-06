using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public class HuggingFaceModelDownloadServiceTests
{
    [TestMethod]
    public async Task GetOperationStatusAsync_CompletedStatus_RegistersCatalog()
    {
        const string operationId = "op-completed";
        using var harness = CreateHarness();
        harness.AdminClient
            .Setup(x => x.StartDownloadAsync(
                It.IsAny<StartModelDownloadRequest>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(operationId, "queued", "router-a", 0, null, "queued"));
        harness.AdminClient
            .Setup(x => x.GetDownloadStatusAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(operationId, "completed", "router-a", 1, null, "done"));
        SetupRuntimeProfile(harness);
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
                RuntimeConfigJson: "{\"routerModelId\":\"router-a\"}",
                IsActive: true,
                DisplayOrder: 1,
                Created: DateTime.UtcNow,
                Updated: null));

        await harness.Service.StartDownloadAsync(CreateCatalogRequest());
        var poll = await harness.Service.GetOperationStatusAsync(operationId);

        poll.Should().NotBeNull();
        poll!.Status.Should().Be("completed");
        harness.SettingsService.Verify(
            x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_FailedStatus_DoesNotRegisterCatalog()
    {
        const string operationId = "op-failed";
        using var harness = CreateHarness();
        harness.AdminClient
            .Setup(x => x.StartDownloadAsync(
                It.IsAny<StartModelDownloadRequest>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(operationId, "queued", "router-a", 0, null, "queued"));
        harness.AdminClient
            .Setup(x => x.GetDownloadStatusAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: operationId,
                Status: "failed",
                RouterModelId: "router-a",
                Progress: 1,
                ErrorMessage: "download failed",
                LogLine: "failed"));
        SetupRuntimeProfile(harness);

        await harness.Service.StartDownloadAsync(CreateCatalogRequest());
        var poll = await harness.Service.GetOperationStatusAsync(operationId);

        poll.Should().NotBeNull();
        poll!.Status.Should().Be("failed");
        harness.SettingsService.Verify(
            x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
            CatalogIsActive: true,
            CatalogDisplayOrder: 1,
            CatalogLoadParamsJson: "{\"model\":\"router-a\"}",
            CatalogParallelToolCalls: false);
    }

    private static void SetupRuntimeProfile(Harness harness)
    {
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

        var serviceProvider = new Mock<IServiceProvider>(MockBehavior.Strict);
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
            AdminClient: adminClient,
            SettingsService: settingsService,
            RuntimeProfileResolver: runtimeProfileResolver);
    }

    private sealed record Harness(
        HuggingFaceModelDownloadService Service,
        Mock<ILlamaRuntimeAdminClient> AdminClient,
        Mock<IApplicationSettingsService> SettingsService,
        Mock<IRuntimeProfileResolver> RuntimeProfileResolver) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

