using System.Collections;
using System.Reflection;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using ChatRole = AntRunner.Chat.Abstractions.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
[DoNotParallelize]
public sealed class ConversationServicePreflightTests
{
    private ApplicationDbContext _dbContext = null!;
    private Guid _userId;
    private Guid _projectId;
    private Guid _notebookId;
    private Guid _conversationId;

    [TestInitialize]
    public void TestInitialize()
    {
        AssistantUtility.ClearAllCache();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _userId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _conversationId = Guid.NewGuid();

        _dbContext.Users.Add(new User { Id = _userId, Email = "preflight@test.local", Name = "Preflight Tester" });
        // Notebook.Project is a required navigation; the InMemory provider's Include treats required
        // navigations as an inner join, so the root conversation query returns null unless a matching
        // Project row exists here.
        var project = new Project { Id = _projectId, Title = "Preflight Project", Slug = "preflight-project" };
        _dbContext.Projects.Add(project);
        var notebook = new Notebook { Id = _notebookId, ProjectId = _projectId, Title = "Preflight Notebook", Project = project };
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "Preflight Conversation",
            Notebook = notebook
        });
        _dbContext.SaveChanges();

        SeedAssistantDefinitionCache("Claude", new AssistantDefinition
        {
            Name = "Claude",
            Model = "gpt-4o-mini",
            Instructions = "You are a test assistant."
        });
    }

    [TestCleanup]
    public void TestCleanup()
    {
        AssistantUtility.ClearAllCache();
        _dbContext.Dispose();
    }

    private ConversationService CreateFixture(
        IConversationHistoryBuilder? historyBuilderOverride = null,
        ConversationStreamRunRegistry? registry = null,
        Mock<IDistributedConversationLock>? lockMock = null,
        ILogger<ConversationService>? logger = null,
        Mock<IConversationBroadcastHub>? hubMock = null)
    {
        var scopeFactory = new TestServiceScopeFactory(_dbContext);

        lockMock ??= new Mock<IDistributedConversationLock>();
        lockMock
            .Setup(l => l.TryAcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid conversationId, string userName, CancellationToken _) =>
                LockAcquisitionResult.Acquired(new ConversationLock
                {
                    ConversationId = conversationId,
                    LockedByUserName = userName,
                    LockedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                }));
        lockMock
            .Setup(l => l.RenewLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lockMock
            .Setup(l => l.ReleaseLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var chatClient = new Mock<IChatCompletionClient>();
        chatClient.SetupGet(c => c.SupportsToolChoiceNone).Returns(true);
        var completedResponse = new ChatCompletionResponse(
            new[] { new ChatChoice(new ChatMessage(ChatRole.Assistant, "Hello from the mock model."), "stop") },
            null);
        chatClient
            .Setup(c => c.GetCompletionAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedResponse);
        chatClient
            .Setup(c => c.StreamCompletionAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<Action<ChatCompletionChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedResponse);

        var chatClientFactory = new Mock<IChatCompletionClientFactory>();
        chatClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>(), It.IsAny<HttpClient>()))
            .Returns(chatClient.Object);

        var chatModelResolver = new Mock<IChatModelResolver>();
        chatModelResolver
            .Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns((string? id) => new ResolvedChatModel(
                string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var contextOptions = new Mock<IContextOptionsService>();
        contextOptions
            .Setup(m => m.BuildContextMessageAsync(It.IsAny<AssistantDefinition>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(x => x["FileStorage:Path"]).Returns("/tmp/preflight-test-storage");

        var (queryService, commandService, historyBuilder, attachmentService) = ConversationTestServices.Create(
            scopeFactory,
            contextOptions.Object,
            Mock.Of<IMarkdownExtractionService>(),
            configuration.Object);
        var (persistence, usageReporter) = ConversationTestServices.CreatePersistence(scopeFactory);

        return ConversationTestServices.CreateConversationService(
            scopeFactory,
            chatModelResolver.Object,
            queryService,
            commandService,
            historyBuilderOverride ?? historyBuilder,
            attachmentService,
            persistence,
            usageReporter,
            chatClientFactory.Object,
            lockMock.Object,
            broadcastHub: hubMock?.Object,
            logger: logger,
            streamRunRegistry: registry);
    }

    private async Task<List<StreamingEvent>> RunStreamAsync(ConversationService service, string instructions = "Hi")
    {
        var events = new List<StreamingEvent>();
        await foreach (var ev in service.SendMessageStreamToConversationAsUserAsync(
            _conversationId,
            new SendMessageRequest { Instructions = instructions, AssistantName = "Claude" },
            _userId))
        {
            events.Add(ev);
        }
        return events;
    }

    private static void SeedAssistantDefinitionCache(string assistantName, AssistantDefinition definition)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, definition)!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[assistantName] = entry;
    }

    [TestMethod]
    public async Task SendMessageStream_HappyPath_EmitsTurnCreatedFirst()
    {
        var service = CreateFixture();

        var events = await RunStreamAsync(service);

        events.Should().NotBeEmpty();
        events[0].EventType.Should().Be(StreamingEventTypes.TurnCreated);
    }

    [TestMethod]
    public async Task SendMessageStream_LogsPreflightTimings()
    {
        var loggerMock = new Mock<ILogger<ConversationService>>();
        var service = CreateFixture(logger: loggerMock.Object);

        await RunStreamAsync(service);

        loggerMock.Verify(l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Preflight timings")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendMessageStream_TurnIsStreamingWithExecutionIdWhenTurnCreatedEmitted()
    {
        var service = CreateFixture();

        // Manually step the stream instead of draining it via RunStreamAsync: the mock chat client
        // resolves the whole turn synchronously, so a fully-drained stream would already show the
        // turn's terminal "completed" status by the time any assertion ran. Stopping right after the
        // first MoveNextAsync captures the DB state at the actual moment turn_created is observable,
        // which is the invariant this test protects.
        await using var enumerator = service.SendMessageStreamToConversationAsUserAsync(
            _conversationId,
            new SendMessageRequest { Instructions = "Hi", AssistantName = "Claude" },
            _userId).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.EventType.Should().Be(StreamingEventTypes.TurnCreated);

        var turn = _dbContext.ConversationTurns.AsNoTracking().Single(t => t.NotebookConversationId == _conversationId);
        turn.ExecutionId.Should().NotBeNull();
        turn.Status.Should().Be("streaming");

        // Drain the remainder so the mocked run completes and releases its lock/registry entries
        // deterministically before the test ends, matching how the other tests in this class finish.
        while (await enumerator.MoveNextAsync())
        {
        }
    }

    [TestMethod]
    public async Task SendMessageStream_HistoryFailureAfterTurnCreated_EmitsErrorAndTerminalizesTurn()
    {
        var lockMock = new Mock<IDistributedConversationLock>();
        var registry = new ConversationStreamRunRegistry();
        var hubMock = new Mock<IConversationBroadcastHub>();
        var broadcasts = new List<StreamingEvent>();
        hubMock
            .Setup(h => h.BroadcastToConversationAsync(It.IsAny<Guid>(), It.IsAny<StreamingEvent>()))
            .Callback((Guid _, StreamingEvent ev) => broadcasts.Add(ev))
            .Returns(Task.CompletedTask);
        var failingHistory = new Mock<IConversationHistoryBuilder>();
        failingHistory
            .Setup(h => h.PrepareMessagesForAssistantAsync(It.IsAny<NotebookConversation>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("history exploded"));
        var service = CreateFixture(
            historyBuilderOverride: failingHistory.Object,
            registry: registry,
            lockMock: lockMock,
            hubMock: hubMock);

        var events = await RunStreamAsync(service);

        events.Should().HaveCount(2);
        events[0].EventType.Should().Be(StreamingEventTypes.TurnCreated);
        events[1].EventType.Should().Be(StreamingEventTypes.Error);

        var turn = _dbContext.ConversationTurns.AsNoTracking().Single(t => t.NotebookConversationId == _conversationId);
        turn.Status.Should().Be("failed");

        registry.RequestCancel(turn.Id).Should().BeFalse("the worker registration must be cleaned up");
        lockMock.Verify(l => l.ReleaseLockAsync(_conversationId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Observers attached via ObserveConversationEventsAsync must receive the same terminal error
        // the requesting client got - they have no handler for conversation_unlocked, so without this
        // broadcast they stay stuck showing a mid-stream turn.
        hubMock.Verify(
            h => h.BroadcastToConversationAsync(_conversationId, It.Is<StreamingEvent>(ev => ev.EventType == StreamingEventTypes.Error)),
            Times.Once);
        broadcasts.Select(b => b.EventType).Should().ContainInOrder(
            StreamingEventTypes.Error,
            StreamingEventTypes.ConversationUnlocked);
    }

    [TestMethod]
    public async Task SendMessageStream_HappyPath_StillCompletesAfterEarlyYieldRestructure()
    {
        var service = CreateFixture();

        var events = await RunStreamAsync(service);

        events[0].EventType.Should().Be(StreamingEventTypes.TurnCreated);
        events.Select(e => e.EventType).Should().Contain(StreamingEventTypes.Complete);
    }
}
