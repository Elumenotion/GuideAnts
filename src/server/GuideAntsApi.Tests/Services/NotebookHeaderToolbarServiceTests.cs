using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.NotebookHeaderToolbar;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookHeaderToolbarServiceTests
{
    [TestMethod]
    public async Task GetToolbarAsync_ExcludesBlockedChatTargetsFromSelectableModelOptions()
    {
        await using var db = CreateDb();
        var project = new Project
        {
            Title = "Project",
            Slug = "project"
        };
        var notebook = new Notebook
        {
            Title = "Notebook",
            Slug = "notebook",
            ProjectId = project.Id,
            Project = project
        };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SettingsModelDto(
                    ModelId: "gemini-2.5-flash",
                    DisplayName: "Gemini 2.5 Flash",
                    Provider: "google-gemini-chat",
                    Description: null,
                    ReasoningChoicesJson: null,
                    RuntimeConfigJson: null,
                    IsActive: true,
                    DisplayOrder: 1,
                    Created: DateTime.UtcNow,
                    Updated: null),
                new SettingsModelDto(
                    ModelId: "gemini-2.5-pro",
                    DisplayName: "Gemini 2.5 Pro",
                    Provider: "google-gemini-chat",
                    Description: null,
                    ReasoningChoicesJson: null,
                    RuntimeConfigJson: null,
                    IsActive: true,
                    DisplayOrder: 2,
                    Created: DateTime.UtcNow,
                    Updated: null)
            ]);
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) => CreateReadyServiceState(serviceId));

        var readiness = new Mock<IRoutingReadinessService>(MockBehavior.Strict);
        readiness
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind) =>
                string.Equals(modelId, "gemini-2.5-flash", StringComparison.Ordinal)
                    ? new ChatTargetReadinessDto(
                        ModelId: modelId,
                        Provider: "google-gemini-chat",
                        Status: "blocked",
                        Blockers:
                        [
                            "PROVIDER_MISSING_FIELDS: provider 'google-gemini-chat' for model 'gemini-2.5-flash' is not a recognized chat provider."
                        ],
                        RuntimeState: null,
                        AssistantUsageCount: 0,
                        ReferenceKind: referenceKind)
                    : new ChatTargetReadinessDto(
                        ModelId: modelId,
                        Provider: "google-gemini-chat",
                        Status: "ready",
                        Blockers: Array.Empty<string>(),
                        RuntimeState: null,
                        AssistantUsageCount: 0,
                        ReferenceKind: referenceKind));

        var chatModelResolver = new Mock<IChatModelResolver>(MockBehavior.Strict);
        chatModelResolver
            .Setup(x => x.Resolve(It.IsAny<string?>()))
            .Returns(new ResolvedChatModel(
                "gemini-2.5-flash",
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    "gemini-2.5-flash",
                    "google-gemini-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var conversations = new Mock<IConversationManager>(MockBehavior.Strict);

        var llamaRuntime = new Mock<INotebookModelRuntimeService>(MockBehavior.Strict);
        llamaRuntime
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "ready"
            });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatDefaults:OverrideAllChatModels"] = "true"
            })
            .Build();

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            configuration,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<NotebookHeaderToolbarService>.Instance);

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);

        toolbar.Chat.EffectiveModelId.Should().Be("gemini-2.5-flash");
        toolbar.Chat.Blockers.Should().ContainSingle()
            .Which.Should().Contain("PROVIDER_MISSING_FIELDS");
        toolbar.Chat.ModelOptions.Select(option => option.ModelId).Should().Equal("gemini-2.5-pro");
        toolbar.Chat.ModelOptions.Should().OnlyContain(option =>
            !string.Equals(option.ModelId, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GetToolbarAsync_KeepsLocalModelsSelectable_WhenOnlyRuntimeStateIsBlocking()
    {
        await using var db = CreateDb();
        var project = new Project
        {
            Title = "Project",
            Slug = "project"
        };
        var notebook = new Notebook
        {
            Title = "Notebook",
            Slug = "notebook",
            ProjectId = project.Id,
            Project = project
        };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SettingsModelDto(
                    ModelId: "qwen-local",
                    DisplayName: "Qwen Local",
                    Provider: "llama-cpp",
                    Description: null,
                    ReasoningChoicesJson: null,
                    RuntimeConfigJson: "{\"routerModelId\":\"qwen-local\"}",
                    IsActive: true,
                    DisplayOrder: 1,
                    Created: DateTime.UtcNow,
                    Updated: null)
            ]);
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) => CreateReadyServiceState(serviceId));

        var readiness = new Mock<IRoutingReadinessService>(MockBehavior.Strict);
        readiness
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind) =>
                new ChatTargetReadinessDto(
                    ModelId: modelId,
                    Provider: "llama-cpp",
                    Status: "blocked",
                    Blockers:
                    [
                        "RUNTIME_STATE: alias 'qwen-local' runtime state is 'unloaded' (expected 'loaded')."
                    ],
                    RuntimeState: "unloaded",
                    AssistantUsageCount: 0,
                    ReferenceKind: referenceKind));

        var chatModelResolver = new Mock<IChatModelResolver>(MockBehavior.Strict);
        chatModelResolver
            .Setup(x => x.Resolve(It.IsAny<string?>()))
            .Returns(new ResolvedChatModel(
                "qwen-local",
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    "qwen-local",
                    "llama-cpp",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var conversations = new Mock<IConversationManager>(MockBehavior.Strict);

        var llamaRuntime = new Mock<INotebookModelRuntimeService>(MockBehavior.Strict);
        llamaRuntime
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "requires_load"
            });

        var configuration = new ConfigurationBuilder().Build();

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            configuration,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<NotebookHeaderToolbarService>.Instance);

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);

        toolbar.Chat.ModelOptions.Select(option => option.ModelId).Should().Equal("qwen-local");
        toolbar.Chat.SupportsLocalRuntimePower.Should().BeTrue();
        toolbar.Chat.Status.Should().Be("requiresLoad");
        toolbar.Chat.Summary.Should().Contain("Qwen Local selected");
        toolbar.Chat.Summary.Should().Contain("No local model is loaded");
        toolbar.Chat.Summary.Should().NotContain("RUNTIME_STATE");
        toolbar.Chat.Summary.Should().NotContain("runtime off");
        toolbar.Chat.Blockers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetToolbarAsync_ExplainsLocalModelSwitch_WhenAnotherLocalModelIsLoaded()
    {
        await using var db = CreateDb();
        var project = new Project
        {
            Title = "Project",
            Slug = "project"
        };
        var notebook = new Notebook
        {
            Title = "Notebook",
            Slug = "notebook",
            ProjectId = project.Id,
            Project = project
        };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SettingsModelDto(
                    ModelId: "qwen-local",
                    DisplayName: "Qwen Local",
                    Provider: "llama-cpp",
                    Description: null,
                    ReasoningChoicesJson: null,
                    RuntimeConfigJson: "{\"routerModelId\":\"qwen-local\"}",
                    IsActive: true,
                    DisplayOrder: 1,
                    Created: DateTime.UtcNow,
                    Updated: null),
                new SettingsModelDto(
                    ModelId: "mistral-local",
                    DisplayName: "Mistral Local",
                    Provider: "llama-cpp",
                    Description: null,
                    ReasoningChoicesJson: null,
                    RuntimeConfigJson: "{\"routerModelId\":\"mistral-local\"}",
                    IsActive: true,
                    DisplayOrder: 2,
                    Created: DateTime.UtcNow,
                    Updated: null)
            ]);
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) => CreateReadyServiceState(serviceId));

        var readiness = new Mock<IRoutingReadinessService>(MockBehavior.Strict);
        readiness
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind) =>
                string.Equals(modelId, "qwen-local", StringComparison.Ordinal)
                    ? new ChatTargetReadinessDto(
                        ModelId: modelId,
                        Provider: "llama-cpp",
                        Status: "blocked",
                        Blockers:
                        [
                            "RUNTIME_STATE: alias 'qwen-local' runtime state is 'unloaded' (expected 'loaded')."
                        ],
                        RuntimeState: "unloaded",
                        AssistantUsageCount: 0,
                        ReferenceKind: referenceKind)
                    : new ChatTargetReadinessDto(
                        ModelId: modelId,
                        Provider: "llama-cpp",
                        Status: "ready",
                        Blockers: Array.Empty<string>(),
                        RuntimeState: "loaded",
                        AssistantUsageCount: 0,
                        ReferenceKind: referenceKind));

        var chatModelResolver = new Mock<IChatModelResolver>(MockBehavior.Strict);
        chatModelResolver
            .Setup(x => x.Resolve(It.IsAny<string?>()))
            .Returns(new ResolvedChatModel(
                "qwen-local",
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    "qwen-local",
                    "llama-cpp",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var conversations = new Mock<IConversationManager>(MockBehavior.Strict);

        var llamaRuntime = new Mock<INotebookModelRuntimeService>(MockBehavior.Strict);
        llamaRuntime
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "requires_load",
                LoadedModels =
                [
                    new ModelDto(
                        "mistral-local",
                        "Mistral Local",
                        Description: null,
                        ReasoningChoicesJson: null,
                        IsActive: true,
                        DisplayOrder: 2,
                        RuntimeConfig: new ModelRuntimeConfigDto("mistral-local", "default", null),
                        SamplingParameterPolicy: null,
                        ReasoningChoices: null,
                        DefaultReasoningChoice: null)
                ]
            });

        var configuration = new ConfigurationBuilder().Build();

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            configuration,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<NotebookHeaderToolbarService>.Instance);

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);

        toolbar.Chat.Status.Should().Be("requiresLoad");
        toolbar.Chat.Summary.Should().Contain("Qwen Local selected");
        toolbar.Chat.Summary.Should().Contain("Mistral Local is currently loaded");
        toolbar.Chat.Summary.Should().Contain("Load Qwen Local to switch");
        toolbar.Chat.Summary.Should().NotContain("runtime off");
        toolbar.Chat.Summary.Should().NotContain("RUNTIME_STATE");
        toolbar.Chat.Blockers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetToolbarAsync_PrefersReadinessRuntimeState_WhenRuntimeCacheIsStale()
    {
        await using var db = CreateDb();
        var project = new Project
        {
            Title = "Project",
            Slug = "project"
        };
        var notebook = new Notebook
        {
            Title = "Notebook",
            Slug = "notebook",
            ProjectId = project.Id,
            Project = project
        };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SettingsModelDto(
                    ModelId: "qwen-local",
                    DisplayName: "Qwen Local",
                    Provider: "llama-cpp",
                    Description: null,
                    ReasoningChoicesJson: null,
                    RuntimeConfigJson: "{\"routerModelId\":\"qwen-local\"}",
                    IsActive: true,
                    DisplayOrder: 1,
                    Created: DateTime.UtcNow,
                    Updated: null)
            ]);
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) => CreateReadyServiceState(serviceId));

        var readiness = new Mock<IRoutingReadinessService>(MockBehavior.Strict);
        readiness
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind) =>
                new ChatTargetReadinessDto(
                    ModelId: modelId,
                    Provider: "llama-cpp",
                    Status: "blocked",
                    Blockers:
                    [
                        "RUNTIME_STATE: alias 'qwen-local' runtime state is 'unloaded' (expected 'loaded')."
                    ],
                    RuntimeState: "unloaded",
                    AssistantUsageCount: 0,
                    ReferenceKind: referenceKind));

        var chatModelResolver = new Mock<IChatModelResolver>(MockBehavior.Strict);
        chatModelResolver
            .Setup(x => x.Resolve(It.IsAny<string?>()))
            .Returns(new ResolvedChatModel(
                "qwen-local",
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    "qwen-local",
                    "llama-cpp",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var conversations = new Mock<IConversationManager>(MockBehavior.Strict);

        var llamaRuntime = new Mock<INotebookModelRuntimeService>(MockBehavior.Strict);
        // Simulate stale cached snapshot claiming ready/local-on while readiness probe says unloaded.
        llamaRuntime
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "ready",
                LoadedModels =
                [
                    new ModelDto(
                        "qwen-local",
                        "Qwen Local",
                        Description: null,
                        ReasoningChoicesJson: null,
                        IsActive: true,
                        DisplayOrder: 1,
                        RuntimeConfig: new ModelRuntimeConfigDto("qwen-local", "default", null),
                        SamplingParameterPolicy: null,
                        ReasoningChoices: null,
                        DefaultReasoningChoice: null)
                ]
            });

        var configuration = new ConfigurationBuilder().Build();

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            configuration,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<NotebookHeaderToolbarService>.Instance);

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);

        toolbar.Chat.SupportsLocalRuntimePower.Should().BeTrue();
        toolbar.Chat.LocalRuntimeOn.Should().BeFalse("readiness reports runtime state as unloaded");
        toolbar.Chat.Status.Should().Be("requiresLoad");
        toolbar.Chat.Blockers.Should().BeEmpty();
        toolbar.Chat.Summary.Should().Contain("Load Qwen Local");
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"notebook-header-toolbar-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static ServiceEditorStateDto CreateReadyServiceState(string serviceId)
    {
        return new ServiceEditorStateDto(
            ServiceId: serviceId,
            ActiveProviderId: "google",
            Providers:
            [
                new ProviderEditorStateDto(
                    ProviderId: "google",
                    ProviderKind: "Cloud",
                    ProviderSection: "GoogleGeminiApi",
                    ModeId: "google",
                    HasExplicitMode: true,
                    IsDefaultMode: true,
                    ConnectionConfigured: true,
                    ConnectionMissingFields: Array.Empty<string>(),
                    CanActivate: true,
                    ActivationBlockers: Array.Empty<string>(),
                    Fields: new Dictionary<string, ProviderFieldValueDto>(),
                    RuntimeDependencies: Array.Empty<RuntimeKeyDto>(),
                    OperativeFields: Array.Empty<string>(),
                    DiagnosticFields: Array.Empty<string>(),
                    FieldMetadata: Array.Empty<ProviderFieldMetadataDto>())
            ],
            Readiness: new ServiceEditorReadinessDto(
                Status: "ready",
                Blockers: Array.Empty<string>(),
                Warnings: Array.Empty<string>()));
    }
}
