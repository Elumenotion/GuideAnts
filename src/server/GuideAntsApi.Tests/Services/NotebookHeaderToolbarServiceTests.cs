using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.NotebookHeaderToolbar;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
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
                    Provider: "google-vertex-chat",
                    Description: null,
                    ReasoningChoicesJson: null,
                    LocalRuntimeJson: null,
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
                    LocalRuntimeJson: null,
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
                        Provider: "google-vertex-chat",
                        Status: "blocked",
                        Blockers:
                        [
                            "PROVIDER_MISSING_FIELDS: provider 'google-vertex-chat' for model 'gemini-2.5-flash' is not a recognized chat provider."
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
            .Returns(new ResolvedChatModel("gemini-2.5-flash", ChatModelReferenceKind.Direct, null, null, null));

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
            !string.Equals(option.Provider, "google-vertex-chat", StringComparison.OrdinalIgnoreCase));
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
                    LocalRuntimeJson: "{\"routerModelId\":\"qwen-local\"}",
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
            .Returns(new ResolvedChatModel("qwen-local", ChatModelReferenceKind.Direct, null, null, null));

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
            DisplayName: serviceId,
            ActiveProviderId: "google",
            Providers:
            [
                new ProviderEditorStateDto(
                    ProviderId: "google",
                    ProviderKind: "Cloud",
                    DisplayName: "Google",
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
