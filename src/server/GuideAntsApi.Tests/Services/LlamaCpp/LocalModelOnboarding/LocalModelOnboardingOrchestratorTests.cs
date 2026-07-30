using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        CreateSettingsModelRequest? createRequest = null;

        settingsService
            .Setup(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateSettingsModelRequest, CancellationToken>((request, _) => createRequest = request)
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

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient
            .Setup(x => x.GetRouterEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaAdminRouterEntriesResponseDto(
            [
                new LlamaAdminRouterEntryDto(
                    Alias: "qwen-local",
                    ModelPath: "/models-local/llama/qwen/model.gguf",
                    MmprojPath: "",
                    HasModelFile: true,
                    HasMmprojFile: false,
                    ContextSize: 8192,
                    CacheRamMib: null,
                    Preset: new Dictionary<string, string> { ["ctx-size"] = "8192" }),
            ]));

        await using var db = CreateDbContext();
        var orchestrator = CreateOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object,
            adminClient: adminClient.Object,
            db: db);

        var request = CreateLocalRequest(LocalModelInstallSources.ExistingAlias);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var result = await orchestrator.OnboardAsync(request, command, CancellationToken.None);

        result.OperationId.Should().BeNull();
        result.AddOperation.Kind.Should().Be("sync");
        result.AddOperation.Status.Should().Be("completed");
        settingsService.Verify(x => x.CreateModelAsync(It.IsAny<CreateSettingsModelRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        runtimeProfileResolver.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        createRequest!.ThinkingControlJson.Should().Be("""{"defaultChoice":"medium","choiceActions":{}}""");
        createRequest.CombineSystemAndDeveloperMessages.Should().BeFalse();
        downloadService.Verify(x => x.StartDownloadAsync(It.IsAny<StartModelDownloadRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        (await db.LocalModelInstallations.CountAsync()).Should().Be(1);
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

        var orchestrator = CreateOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object);

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

        var orchestrator = CreateOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object);

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var result = await orchestrator.OnboardAsync(request, command, CancellationToken.None);

        result.OperationId.Should().Be("op-existing");
        result.AddOperation.Kind.Should().Be("async");
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_LegacyDownload_DelegatesToDownloadService()
    {
        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var downloadService = new Mock<IHuggingFaceModelDownloadService>(MockBehavior.Strict);

        downloadService
            .Setup(x => x.GetOperationStatusAsync("op-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-123",
                Status: "completed",
                RouterModelId: "qwen-local",
                Progress: 1,
                ErrorMessage: null,
                LogLine: "done"));

        var orchestrator = CreateOrchestrator(
            settingsService.Object,
            runtimeProfileResolver.Object,
            downloadService.Object);

        var op = await orchestrator.GetOperationStatusAsync("op-123", CancellationToken.None);

        op.Should().NotBeNull();
        op!.Status.Should().Be("completed");
        downloadService.Verify(x => x.GetOperationStatusAsync("op-123", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static LocalModelOnboardingOrchestrator CreateOrchestrator(
        IApplicationSettingsService settingsService,
        IRuntimeProfileResolver runtimeProfileResolver,
        IHuggingFaceModelDownloadService downloadService,
        ICuratedInstallResolver? curatedInstallResolver = null,
        ILocalModelOperationService? operationService = null,
        ILlamaRuntimeAdminClient? adminClient = null,
        ApplicationDbContext? db = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        return new LocalModelOnboardingOrchestrator(
            settingsService,
            runtimeProfileResolver,
            downloadService,
            curatedInstallResolver ?? new Mock<ICuratedInstallResolver>(MockBehavior.Strict).Object,
            operationService ?? new Mock<ILocalModelOperationService>(MockBehavior.Strict).Object,
            new Mock<ICustomInstallResolver>(MockBehavior.Strict).Object,
            new Mock<ILocalModelLifecycleOperationService>(MockBehavior.Strict).Object,
            adminClient ?? new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            db ?? CreateDbContext(),
            scopeFactory ?? CreateScopeFactory(operationService),
            NullLogger<LocalModelOnboardingOrchestrator>.Instance);
    }

    private static IServiceScopeFactory CreateScopeFactory(ILocalModelOperationService? operationService = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => operationService ?? new Mock<ILocalModelOperationService>(MockBehavior.Strict).Object);
        services.AddScoped(_ => new Mock<ILocalModelLifecycleOperationService>(MockBehavior.Strict).Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
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
                }),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>());
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
            ProviderConfig: CreateRowOwnedChatBehavior(),
            Install: new AddModelInstallDto(
                Source: source,
                RouterModelId: "qwen-local",
                RuntimeProfileId: string.Equals(source, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase)
                    ? "qwen3_6"
                    : null,
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

    private static JsonObject CreateRowOwnedChatBehavior() =>
        JsonNode.Parse("""{"samplingParametersJson":"{}","thinkingControlJson":"{\"defaultChoice\":\"medium\",\"choiceActions\":{}}","requestFieldsWhenToolsPresentJson":"{}","combineSystemAndDeveloperMessages":false}""")!.AsObject();
}
