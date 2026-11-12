using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.NotebookHeaderToolbar;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Text;

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
        SetupToolbarServiceModesDefaults(settings);
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
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<RoutingProbeMode>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind, RoutingProbeMode _) =>
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
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
        var chatDefaults = CreateDefaultChatDefaultsStore(overrideAllChatModels: true);

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            chatDefaults,
            configuration,
            new LocalEndpointCache(new NoopProbe()),
            new LocalInventoryMirror(),
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
        SetupToolbarServiceModesDefaults(settings);
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
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<RoutingProbeMode>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind, RoutingProbeMode _) =>
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
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "requires_load"
            });

        var configuration = new ConfigurationBuilder().Build();
        var chatDefaults = CreateDefaultChatDefaultsStore(overrideAllChatModels: false);

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            chatDefaults,
            configuration,
            new LocalEndpointCache(new NoopProbe()),
            new LocalInventoryMirror(),
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
    public async Task GetToolbarAsync_ExposesEffectiveModelSource_WhenAssistantDefaultsToGlobalModel()
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
        SetupToolbarServiceModesDefaults(settings);
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
                    RuntimeConfigJson: "{\\\"routerModelId\\\":\\\"qwen-local\\\"}",
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
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<RoutingProbeMode>()))
            .ReturnsAsync(new ChatTargetReadinessDto(
                "qwen-local",
                "llama-cpp",
                "ready",
                [],
                null,
                0,
                "defaultedTo"));

        var chatModelResolver = new Mock<IChatModelResolver>(MockBehavior.Strict);
        chatModelResolver
            .Setup(x => x.Resolve(It.IsAny<string?>()))
            .Returns(new ResolvedChatModel(
                "qwen-local",
                ChatModelReferenceKind.DefaultedTo,
                new ResolvedExecutionPolicy(
                    "qwen-local",
                    "llama-cpp",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var conversations = new Mock<IConversationManager>(MockBehavior.Strict);

        var llamaRuntime = new Mock<INotebookModelRuntimeService>(MockBehavior.Strict);
        llamaRuntime
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "requires_load"
            });

        var configuration = new ConfigurationBuilder().Build();
        var chatDefaults = CreateDefaultChatDefaultsStore(overrideAllChatModels: false);

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            chatDefaults,
            configuration,
            new LocalEndpointCache(new NoopProbe()),
            new LocalInventoryMirror(),
            NullLogger<NotebookHeaderToolbarService>.Instance);

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);

        toolbar.Chat.OverrideAllChatModels.Should().BeFalse();
        toolbar.Chat.EffectiveModelSource.Should().Be("defaultedTo");
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
        SetupToolbarServiceModesDefaults(settings);
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
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<RoutingProbeMode>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind, RoutingProbeMode _) =>
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
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
                        RuntimeConfig: new ModelRuntimeConfigDto("mistral-local"),
                        SamplingParameterPolicy: null,
                        ReasoningChoices: null,
                        DefaultReasoningChoice: null)
                ]
            });

        var configuration = new ConfigurationBuilder().Build();
        var chatDefaults = CreateDefaultChatDefaultsStore(overrideAllChatModels: false);

        // Plane C: the alias mirror last reported mistral-local loaded (this used to arrive
        // via the live GetRuntimeStatusAsync stub, which the config path no longer calls).
        var mirror = new LocalInventoryMirror();
        mirror.SetLive("llama",
            [new NotebookToolbarLocalModelOptionDto("mistral-local", "mistral-local", true, IsActive: true)],
            DateTime.UtcNow);

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            chatDefaults,
            configuration,
            new LocalEndpointCache(new NoopProbe()),
            mirror,
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
        SetupToolbarServiceModesDefaults(settings);
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
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<RoutingProbeMode>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind, RoutingProbeMode _) =>
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
            .Setup(x => x.GetRuntimeStatusAsync(notebook.Id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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
                        RuntimeConfig: new ModelRuntimeConfigDto("qwen-local"),
                        SamplingParameterPolicy: null,
                        ReasoningChoices: null,
                        DefaultReasoningChoice: null)
                ]
            });

        var configuration = new ConfigurationBuilder().Build();
        var chatDefaults = CreateDefaultChatDefaultsStore(overrideAllChatModels: false);

        var sut = new NotebookHeaderToolbarService(
            db,
            settings.Object,
            readiness.Object,
            chatModelResolver.Object,
            conversations.Object,
            llamaRuntime.Object,
            chatDefaults,
            configuration,
            new LocalEndpointCache(new NoopProbe()),
            new LocalInventoryMirror(),
            NullLogger<NotebookHeaderToolbarService>.Instance);

        // T-U5b retarget: the toolbar (configuration) no longer probes live llama state, so
        // "prefers live readiness over the stale runtime cache" is asserted on the use-path
        // surface that still keeps Live: GetChatReadinessAsync (R3).
        var readinessDto = await sut.GetChatReadinessAsync(notebook.Id, conversationId: null);

        readinessDto.SupportsLocalRuntimePower.Should().BeTrue();
        readinessDto.LocalRuntimeOn.Should().BeFalse("readiness reports runtime state as unloaded");
        readinessDto.Blockers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetToolbarAsync_ReportsTtsInProgress_WhenLocalEngineIsLoading()
    {
        await using var db = CreateDb();
        var notebook = await SeedNotebookAsync(db);

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        SetupToolbarServiceModesDefaults(settings);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) =>
                string.Equals(serviceId, RoutedServiceNames.SpeechSynthesis, StringComparison.Ordinal)
                    ? CreateLocalServiceState(
                        RoutedServiceNames.SpeechSynthesis,
                        ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
                        "LocalServiceHosts:SpeechSynthesisBaseUrl")
                    : CreateReadyServiceState(serviceId));

        var sut = CreateSut(
            db,
            notebook.Id,
            settings.Object,
            endpointCache: CacheWith(TtsReadyUrl, EndpointAvailability.Reachable, "loading"),
            inventoryMirror: MirrorWith(RoutedServiceNames.SpeechSynthesis, LocalModel("chatterbox", complete: true, active: true)));

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);
        var tts = toolbar.Services.Single(service => service.Kind == "tts");

        tts.Status.Should().Be("inProgress");
        tts.InProgressState.Should().Be("loading");
        tts.LocalRuntimeOn.Should().BeFalse();
        tts.Summary.Should().Contain("loading");
    }

    [TestMethod]
    public async Task GetToolbarAsync_ReportsTtsRequiresLoad_WhenLocalEngineIsUnloaded()
    {
        await using var db = CreateDb();
        var notebook = await SeedNotebookAsync(db);

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        SetupToolbarServiceModesDefaults(settings);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) =>
                string.Equals(serviceId, RoutedServiceNames.SpeechSynthesis, StringComparison.Ordinal)
                    ? CreateLocalServiceState(
                        RoutedServiceNames.SpeechSynthesis,
                        ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
                        "LocalServiceHosts:SpeechSynthesisBaseUrl")
                    : CreateReadyServiceState(serviceId));

        var sut = CreateSut(
            db,
            notebook.Id,
            settings.Object,
            endpointCache: CacheWith(TtsReadyUrl, EndpointAvailability.Reachable, "unloaded"),
            inventoryMirror: MirrorWith(RoutedServiceNames.SpeechSynthesis, LocalModel("chatterbox", complete: true, active: true)));

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);
        var tts = toolbar.Services.Single(service => service.Kind == "tts");

        tts.Status.Should().Be("requiresLoad");
        tts.InProgressState.Should().BeNull();
        tts.Summary.Should().Contain("Load the local model");
    }

    [TestMethod]
    public async Task GetToolbarAsync_MarksIncompleteLocalTtsModels_WhenInventoryReportsIncomplete()
    {
        await using var db = CreateDb();
        var notebook = await SeedNotebookAsync(db);

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        SetupToolbarServiceModesDefaults(settings);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) =>
                string.Equals(serviceId, RoutedServiceNames.SpeechSynthesis, StringComparison.Ordinal)
                    ? CreateLocalServiceState(
                        RoutedServiceNames.SpeechSynthesis,
                        ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
                        "LocalServiceHosts:SpeechSynthesisBaseUrl")
                    : CreateReadyServiceState(serviceId));

        var sut = CreateSut(
            db,
            notebook.Id,
            settings.Object,
            endpointCache: CacheWith(TtsReadyUrl, EndpointAvailability.Reachable, "unloaded"),
            inventoryMirror: MirrorWith(RoutedServiceNames.SpeechSynthesis,
                LocalModel("chatterbox", complete: true, active: true),
                LocalModel("OmniVoice", complete: false, active: false)));

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);
        var tts = toolbar.Services.Single(service => service.Kind == "tts");

        tts.LocalModelOptions.Should().Contain(option =>
            option.ModelRef == "chatterbox" && option.IsComplete);
        tts.LocalModelOptions.Should().Contain(option =>
            option.ModelRef == "OmniVoice" && !option.IsComplete);
    }

    [TestMethod]
    public async Task GetToolbarAsync_MarksActiveImageBundle_FromPersistedServiceModes()
    {
        await using var db = CreateDb();
        var notebook = await SeedNotebookAsync(db);
        const string bundleId = "FLUX.2-dev";

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        SetupToolbarServiceModesDefaults(settings);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) =>
                string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
                    ? CreateLocalServiceState(
                        RoutedServiceNames.ImageGeneration,
                        ServiceProviderIds.ImageGenerationLocalSdHttp,
                        "LocalServiceHosts:ImageGenerationBaseUrl")
                    : CreateReadyServiceState(serviceId));
        settings
            .Setup(x => x.GetServiceModesAsync(RoutedServiceNames.ImageGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    RoutedServiceNames.ImageGeneration,
                    "local",
                    "LocalServiceHosts:ImageGenerationBaseUrl",
                    bundleId,
                    null,
                    Enabled: true,
                    IsDefault: true),
            ]);

        var sut = CreateSut(
            db,
            notebook.Id,
            settings.Object,
            endpointCache: CacheWith(SdHealthUrl, EndpointAvailability.Reachable, "loaded"),
            inventoryMirror: MirrorWith(RoutedServiceNames.ImageGeneration,
                LocalModel(bundleId, complete: true, active: true),
                LocalModel("flux2-klein-4b", complete: true, active: false)));

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);
        var image = toolbar.Services.Single(service => service.Kind == "image");

        image.LocalModelOptions.Should().Contain(option =>
            option.ModelRef == bundleId && option.IsActive);
        image.LocalModelOptions.Single(option => option.IsActive).DisplayLabel.Should().Be($"{bundleId} (active)");
        image.Selection!.ResourceId.Should().Be(bundleId);
        image.Summary.Should().Contain(bundleId);
    }

    [TestMethod]
    public async Task GetToolbarAsync_CloudProvidersDoNotPresentSavedLocalModelsAsActive()
    {
        await using var db = CreateDb();
        var notebook = await SeedNotebookAsync(db);

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        SetupToolbarServiceModesDefaults(settings);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) => serviceId switch
            {
                RoutedServiceNames.ImageGeneration => CreateCloudServiceStateWithLocalMode(
                    serviceId,
                    ServiceProviderIds.ImageGenerationLocalSdHttp,
                    "LocalServiceHosts:ImageGenerationBaseUrl"),
                RoutedServiceNames.SpeechSynthesis => CreateCloudServiceStateWithLocalMode(
                    serviceId,
                    ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
                    "LocalServiceHosts:SpeechSynthesisBaseUrl"),
                RoutedServiceNames.SpeechTranscription => CreateCloudServiceStateWithLocalMode(
                    serviceId,
                    ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
                    "LocalServiceHosts:SpeechTranscriptionBaseUrl"),
                _ => CreateReadyServiceState(serviceId),
            });

        var sut = CreateSut(
            db,
            notebook.Id,
            settings.Object,
            endpointCache: new LocalEndpointCache(new NoopProbe()),
            inventoryMirror: SeedMirrorForAll());

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);

        foreach (var service in toolbar.Services)
        {
            service.ActiveProviderId.Should().Be("google");
            service.SupportsLocalRuntimePower.Should().BeFalse();
            service.LocalRuntimeOn.Should().BeFalse();
            service.Selection.Should().BeNull();
            service.LocalModelOptions.Should().ContainSingle();
            service.LocalModelOptions[0].IsActive.Should().BeFalse();
            service.LocalModelOptions[0].DisplayLabel.Should().NotContain("(active)");
        }
    }

    [TestMethod]
    public async Task GetToolbarAsync_CloudOnlyImage_DoesNotFailWhenSdReportsBundle()
    {
        await using var db = CreateDb();
        var notebook = await SeedNotebookAsync(db);
        const string bundleId = "FLUX.2-dev";

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        SetupToolbarServiceModesDefaults(settings);
        settings
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingsModelDto>());
        settings
            .Setup(x => x.GetServiceEditorStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serviceId, CancellationToken _) =>
                string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
                    ? CreateReadyServiceState(serviceId)
                    : CreateReadyServiceState(serviceId));

        var sut = CreateSut(
            db,
            notebook.Id,
            settings.Object,
            endpointCache: new LocalEndpointCache(new NoopProbe()),
            inventoryMirror: SeedMirrorOnlyImage(bundleId));

        var toolbar = await sut.GetToolbarAsync(notebook.Id, conversationId: null);
        var image = toolbar.Services.Single(service => service.Kind == "image");

        image.LocalModelOptions.Should().BeEmpty();
        image.Selection.Should().BeNull();
        settings.Verify(
            x => x.SetServiceModeModelIdAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void SetupToolbarServiceModesDefaults(Mock<IApplicationSettingsService> settings)
    {
        settings
            .Setup(x => x.GetServiceModesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ServiceModeDto>());
    }

    private static async Task<Notebook> SeedNotebookAsync(ApplicationDbContext db)
    {
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
        return notebook;
    }

    private static NotebookHeaderToolbarService CreateSut(
        ApplicationDbContext db,
        Guid notebookId,
        IApplicationSettingsService settings,
        IRoutingReadinessService? readiness = null,
        IChatModelResolver? chatModelResolver = null,
        IConversationManager? conversations = null,
        INotebookModelRuntimeService? llamaRuntime = null,
        IConfiguration? configuration = null,
        IChatDefaultsStore? chatDefaultsStore = null,
        ILocalEndpointCache? endpointCache = null,
        ILocalInventoryMirror? inventoryMirror = null)
{
    endpointCache ??= new LocalEndpointCache(new NoopProbe());
    inventoryMirror ??= new LocalInventoryMirror();
        var readinessMock = readiness ?? CreateDefaultReadinessMock().Object;
        var chatModelResolverMock = chatModelResolver ?? CreateDefaultChatModelResolver().Object;
        var conversationsMock = conversations ?? new Mock<IConversationManager>(MockBehavior.Strict).Object;
        var llamaRuntimeMock = llamaRuntime ?? CreateDefaultLlamaRuntime(notebookId).Object;
        var config = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatDefaults:OverrideAllChatModels"] = "true",
                ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110",
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8111",
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8112",
            })
            .Build();
        var chatDefaults = chatDefaultsStore ?? CreateDefaultChatDefaultsStore(overrideAllChatModels: true);
        return new NotebookHeaderToolbarService(
            db,
            settings,
            readinessMock,
            chatModelResolverMock,
            conversationsMock,
            llamaRuntimeMock,
            chatDefaults,
            config,
            endpointCache,
            inventoryMirror,
            NullLogger<NotebookHeaderToolbarService>.Instance);
    }

    /// <summary>
    /// Probe fake that never touches the network; used where a test does not seed the
    /// cache itself. A probe should never run — TryGet performs no I/O (T-U1).
    /// </summary>
    private sealed class NoopProbe : ILocalEndpointProbe
    {
        public Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("toolbar tests must not probe; seed the cache instead");
    }


    // Literal Plane C URLs matching CreateSut's default configuration:
    // SpeechSynthesis :8110 -> /tts, SpeechTranscription :8111 -> /asr, ImageGeneration :8112 -> /sd.
    private const string TtsReadyUrl = "http://localhost:8110/tts/ready";
    private const string SdHealthUrl = "http://localhost:8112/sd/health";

    private static NotebookToolbarLocalModelOptionDto LocalModel(string modelRef, bool complete = true, bool active = false) =>
        new(modelRef, modelRef, complete, active);

    private static LocalEndpointCache CacheWith(string readinessUrl, EndpointAvailability availability, string? runtimeState = null)
    {
        var cache = new LocalEndpointCache(new NoopProbe());
        cache.MarkObserved(readinessUrl, new EndpointObservation(
            readinessUrl, availability, DateTime.UtcNow, 5, null, 0, runtimeState));
        return cache;
    }

    private static LocalInventoryMirror MirrorWith(string serviceId, params NotebookToolbarLocalModelOptionDto[] models)
    {
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(serviceId, models, DateTime.UtcNow);
        return mirror;
    }

    private static LocalInventoryMirror SeedMirrorForAll()
    {
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.ImageGeneration,
            [new NotebookToolbarLocalModelOptionDto("saved-bundle", "saved-bundle", true, false)], DateTime.UtcNow);
        mirror.SetLive(RoutedServiceNames.SpeechSynthesis,
            [new NotebookToolbarLocalModelOptionDto("saved-model", "saved-model", true, false)], DateTime.UtcNow);
        mirror.SetLive(RoutedServiceNames.SpeechTranscription,
            [new NotebookToolbarLocalModelOptionDto("saved-model", "saved-model", true, false)], DateTime.UtcNow);
        return mirror;
    }

    private static LocalInventoryMirror SeedMirrorOnlyImage(string bundleId)
    {
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.ImageGeneration,
            [new NotebookToolbarLocalModelOptionDto(bundleId, bundleId, true, true)], DateTime.UtcNow);
        return mirror;
    }

    private static Mock<IRoutingReadinessService> CreateDefaultReadinessMock()
    {
        var readiness = new Mock<IRoutingReadinessService>(MockBehavior.Strict);
        readiness
            .Setup(x => x.ProbeChatTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<RoutingProbeMode>()))
            .ReturnsAsync((string modelId, CancellationToken _, string referenceKind, RoutingProbeMode _) =>
                new ChatTargetReadinessDto(
                    ModelId: modelId,
                    Provider: "google-gemini-chat",
                    Status: "ready",
                    Blockers: Array.Empty<string>(),
                    RuntimeState: null,
                    AssistantUsageCount: 0,
                    ReferenceKind: referenceKind));
        return readiness;
    }

    private static Mock<IChatModelResolver> CreateDefaultChatModelResolver()
    {
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
        return chatModelResolver;
    }

    private static IChatDefaultsStore CreateDefaultChatDefaultsStore(bool overrideAllChatModels)
    {
        var store = new Mock<IChatDefaultsStore>();
        store.Setup(x => x.Current).Returns(new ChatDefaultsSnapshot(
            DefaultModelId: null,
            OverrideAllChatModels: overrideAllChatModels,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null));
        store.Setup(x => x.RefreshAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store.Object;
    }

    private static Mock<INotebookModelRuntimeService> CreateDefaultLlamaRuntime(Guid notebookId)
    {
        var llamaRuntime = new Mock<INotebookModelRuntimeService>(MockBehavior.Strict);
        llamaRuntime
            .Setup(x => x.GetRuntimeStatusAsync(notebookId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new NotebookLlamaRuntimeStatusDto
            {
                State = "ready"
            });
        return llamaRuntime;
    }

    private static IHttpClientFactory CreateHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new StubHttpClientFactory(responder);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHttpMessageHandler(responder));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static ServiceEditorStateDto CreateLocalServiceState(
        string serviceId,
        string localProviderId,
        string localProviderSection)
    {
        return new ServiceEditorStateDto(
            ServiceId: serviceId,
            ActiveProviderId: localProviderId,
            Providers:
            [
                new ProviderEditorStateDto(
                    ProviderId: localProviderId,
                    ProviderKind: "LocalHttp",
                    ProviderSection: localProviderSection,
                    ModeId: "local",
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

    private static ServiceEditorStateDto CreateCloudServiceStateWithLocalMode(
        string serviceId,
        string localProviderId,
        string localProviderSection)
    {
        var cloud = CreateReadyServiceState(serviceId);
        var local = new ProviderEditorStateDto(
            ProviderId: localProviderId,
            ProviderKind: "LocalHttp",
            ProviderSection: localProviderSection,
            ModeId: "local",
            HasExplicitMode: true,
            IsDefaultMode: false,
            ConnectionConfigured: true,
            ConnectionMissingFields: Array.Empty<string>(),
            CanActivate: true,
            ActivationBlockers: Array.Empty<string>(),
            Fields: new Dictionary<string, ProviderFieldValueDto>(),
            RuntimeDependencies: Array.Empty<RuntimeKeyDto>(),
            OperativeFields: Array.Empty<string>(),
            DiagnosticFields: Array.Empty<string>(),
            FieldMetadata: Array.Empty<ProviderFieldMetadataDto>());

        return cloud with { Providers = [.. cloud.Providers, local] };
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
