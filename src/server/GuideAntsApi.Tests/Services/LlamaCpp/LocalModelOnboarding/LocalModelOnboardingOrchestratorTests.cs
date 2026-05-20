using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp.LocalModelOnboarding;

[TestClass]
public class LocalModelOnboardingOrchestratorTests
{
    [TestMethod]
    public async Task OnboardAsync_ExistingAlias_ReturnsSyncResult()
    {
        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var downloadService = new Mock<IHuggingFaceModelDownloadService>(MockBehavior.Strict);

        runtimeProfileResolver
            .Setup(x => x.ResolveAsync("qwen3_6", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRuntimeProfile());

        settingsService
            .Setup(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsModelDto(
                ModelId: "qwen-local",
                DisplayName: "Qwen Local",
                Provider: "llama-cpp",
                Description: null,
                ReasoningChoicesJson: null,
                RuntimeConfigJson: "{}",
                IsActive: true,
                DisplayOrder: null,
                Created: DateTime.UtcNow,
                Updated: null));

        var orchestrator = new LocalModelOnboardingOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object,
            NullLogger<LocalModelOnboardingOrchestrator>.Instance);

        var request = CreateLocalRequest(LocalModelInstallSources.ExistingAlias);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var result = await orchestrator.OnboardAsync(request, command, CancellationToken.None);

        result.OperationId.Should().BeNull();
        result.AddOperation.Kind.Should().Be("sync");
        result.AddOperation.Status.Should().Be("completed");
        settingsService.Verify(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        downloadService.Verify(x => x.StartDownloadAsync(It.IsAny<StartModelDownloadRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task OnboardAsync_HuggingFace_ReturnsAsyncResult()
    {
        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var downloadService = new Mock<IHuggingFaceModelDownloadService>(MockBehavior.Strict);

        runtimeProfileResolver
            .Setup(x => x.ResolveAsync("qwen3_6", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRuntimeProfile());

        downloadService
            .Setup(x => x.StartDownloadAsync(It.IsAny<StartModelDownloadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-123",
                Status: "queued",
                RouterModelId: "qwen-local",
                Progress: 0,
                ErrorMessage: null,
                LogLine: "queued"));

        var orchestrator = new LocalModelOnboardingOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object,
            NullLogger<LocalModelOnboardingOrchestrator>.Instance);

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var result = await orchestrator.OnboardAsync(request, command, CancellationToken.None);

        result.OperationId.Should().Be("op-123");
        result.AddOperation.Kind.Should().Be("async");
        result.AddOperation.Status.Should().Be("inProgress");
        settingsService.Verify(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task OnboardAsync_HuggingFaceConflictOperation_IsReused()
    {
        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var downloadService = new Mock<IHuggingFaceModelDownloadService>(MockBehavior.Strict);

        runtimeProfileResolver
            .Setup(x => x.ResolveAsync("qwen3_6", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRuntimeProfile());

        downloadService
            .Setup(x => x.StartDownloadAsync(It.IsAny<StartModelDownloadRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlamaRuntimeAdminConflictException(new ModelDownloadOperationDto(
                OperationId: "op-existing",
                Status: "downloading",
                RouterModelId: "qwen-local",
                Progress: 0.4,
                ErrorMessage: null,
                LogLine: "downloading")));

        var orchestrator = new LocalModelOnboardingOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object,
            NullLogger<LocalModelOnboardingOrchestrator>.Instance);

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var result = await orchestrator.OnboardAsync(request, command, CancellationToken.None);

        result.OperationId.Should().Be("op-existing");
        result.AddOperation.Kind.Should().Be("async");
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_Completed_RegistersCatalogOnce()
    {
        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var downloadService = new Mock<IHuggingFaceModelDownloadService>(MockBehavior.Strict);

        runtimeProfileResolver
            .Setup(x => x.ResolveAsync("qwen3_6", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRuntimeProfile());

        downloadService
            .Setup(x => x.StartDownloadAsync(It.IsAny<StartModelDownloadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-123",
                Status: "queued",
                RouterModelId: "qwen-local",
                Progress: 0,
                ErrorMessage: null,
                LogLine: "queued"));

        downloadService
            .Setup(x => x.GetOperationStatusAsync("op-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-123",
                Status: "completed",
                RouterModelId: "qwen-local",
                Progress: 1,
                ErrorMessage: null,
                LogLine: "done"));

        settingsService
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());

        settingsService
            .Setup(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsModelDto(
                ModelId: "qwen-local",
                DisplayName: "Qwen Local",
                Provider: "llama-cpp",
                Description: null,
                ReasoningChoicesJson: null,
                RuntimeConfigJson: "{}",
                IsActive: true,
                DisplayOrder: null,
                Created: DateTime.UtcNow,
                Updated: null));

        var orchestrator = new LocalModelOnboardingOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object,
            NullLogger<LocalModelOnboardingOrchestrator>.Instance);

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        await orchestrator.OnboardAsync(request, command, CancellationToken.None);
        var op = await orchestrator.GetOperationStatusAsync("op-123", CancellationToken.None);

        op.Should().NotBeNull();
        op!.Status.Should().Be("completed");
        settingsService.Verify(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RuntimeProfileData CreateRuntimeProfile()
    {
        return new RuntimeProfileData(
            ProfileId: "qwen3_6",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingParameters: new Dictionary<string, SamplingParameterDefinition>(),
            ThinkingControl: new ThinkingControl(
                "medium",
                new Dictionary<string, IReadOnlyList<ThinkingAction>>
                {
                    ["minimal"] = Array.Empty<ThinkingAction>(),
                    ["medium"] = Array.Empty<ThinkingAction>(),
                }));
    }

    private static AddModelRequest CreateLocalRequest(string source)
    {
        return new AddModelRequest(
            Provider: "llama-cpp",
            Catalog: new AddModelCatalogDto(
                ModelId: "qwen-local",
                DisplayName: "Qwen Local",
                Description: null,
                DisplayOrder: null,
                IsActive: true),
            ProviderConfig: null,
            Install: new AddModelInstallDto(
                Source: source,
                RouterModelId: "qwen-local",
                RuntimeProfileId: "qwen3_6",
                HuggingFace: string.Equals(source, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase)
                    ? new AddModelInstallHuggingFaceDto(
                        Repository: "unsloth/Qwen3.6-9B-GGUF",
                        QuantIncludePattern: "*Q5_K_M*",
                        MmprojIncludePattern: string.Empty,
                        TargetDirectory: "qwen-local")
                    : null,
                ExistingAlias: string.Equals(source, LocalModelInstallSources.ExistingAlias, StringComparison.OrdinalIgnoreCase)
                    ? new AddModelInstallExistingAliasDto("qwen-local")
                    : null));
    }
}
