using System.Collections;
using System.Reflection;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ChatRole = AntRunner.Chat.Abstractions.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
[DoNotParallelize]
public sealed class ConversationRecallWiringTests
{
    private const string AssistantName = "Claude";

    private ApplicationDbContext _dbContext = null!;
    private Guid _userId;
    private Guid _projectId;
    private Guid _notebookId;
    private Guid _conversationId;
    private List<ChatCompletionRequest> _capturedRequests = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        AssistantUtility.ClearAllCache();
        ToolContractRegistry.RefreshContracts();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _userId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _conversationId = Guid.NewGuid();
        _capturedRequests = [];

        SeedAssistantDefinitionCache(
            AssistantName, new AssistantDefinition { Name = AssistantName, Model = "gpt-4o-mini" });
    }

    [TestCleanup]
    public void TestCleanup()
    {
        AssistantUtility.ClearAllCache();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task SendMessageStream_ConversationWithNoBoundary_DoesNotOfferRecall()
    {
        await SeedConversationAsync(boundary: null);

        await RunStreamAsync();

        AdvertisedToolNames().Should().NotContain("conversation_recall");
    }

    [TestMethod]
    public async Task SendMessageStream_CompactedConversation_OffersRecall()
    {
        await SeedConversationAsync(boundary: 2);

        await RunStreamAsync();

        AdvertisedToolNames().Should().Contain("conversation_recall",
            "a compacted conversation must be able to reach what the summary replaced");
    }

    private IEnumerable<string> AdvertisedToolNames() =>
        _capturedRequests
            .SelectMany(r => r.Tools ?? [])
            .Select(t => t.Function?.Name ?? string.Empty);

    private async Task SeedConversationAsync(int? boundary)
    {
        _dbContext.Users.Add(new User { Id = _userId, Email = "recall-wiring@test.local", Name = "Recall Wiring Tester" });
        // Notebook.Project is a required navigation; the InMemory provider's Include treats required
        // navigations as an inner join, so the root conversation query returns null unless a matching
        // Project row exists here.
        var project = new Project { Id = _projectId, Title = "Recall Wiring Project", Slug = "recall-wiring-project" };
        _dbContext.Projects.Add(project);
        var notebook = new Notebook { Id = _notebookId, ProjectId = _projectId, Title = "Recall Wiring Notebook", Slug = "recall-wiring-notebook", Project = project };
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Notebook = notebook,
            Title = "Recall Wiring Conversation",
            CompactionBoundaryTurnIndex = boundary
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task RunStreamAsync()
    {
        var service = CreateFixture();
        await foreach (var _ in service.SendMessageStreamToConversationAsUserAsync(
            _conversationId,
            new SendMessageRequest { Instructions = "Hi", AssistantName = AssistantName },
            _userId))
        {
            // drain
        }
    }

    private ConversationService CreateFixture()
    {
        var scopeFactory = new TestServiceScopeFactory(_dbContext);

        var lockMock = new Mock<IDistributedConversationLock>();
        lockMock.Setup(l => l.TryAcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string userName, CancellationToken _) =>
                LockAcquisitionResult.Acquired(new ConversationLock
                {
                    ConversationId = id,
                    LockedByUserName = userName,
                    LockedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                }));
        lockMock.Setup(l => l.RenewLockAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lockMock.Setup(l => l.ReleaseLockAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var completedResponse = new ChatCompletionResponse(
            new[] { new ChatChoice(new ChatMessage(ChatRole.Assistant, "Hello from the mock model."), "stop") },
            null);

        var chatClient = new Mock<IChatCompletionClient>();
        chatClient.SetupGet(c => c.SupportsToolChoiceNone).Returns(true);
        chatClient.Setup(c => c.GetCompletionAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ChatCompletionRequest r, CancellationToken _) => _capturedRequests.Add(r))
            .ReturnsAsync(completedResponse);
        chatClient.Setup(c => c.StreamCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<Action<ChatCompletionChunk>>(), It.IsAny<CancellationToken>()))
            .Callback((ChatCompletionRequest r, Action<ChatCompletionChunk> _, CancellationToken __) => _capturedRequests.Add(r))
            .ReturnsAsync(completedResponse);

        var chatClientFactory = new Mock<IChatCompletionClientFactory>();
        chatClientFactory.Setup(f => f.CreateClient(It.IsAny<string>(), It.IsAny<HttpClient>()))
            .Returns(chatClient.Object);

        var chatModelResolver = new Mock<IChatModelResolver>();
        chatModelResolver.Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns((string? id) => new ResolvedChatModel(
                string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var contextOptions = new Mock<IContextOptionsService>();
        contextOptions.Setup(m => m.BuildContextMessageAsync(
                It.IsAny<AssistantDefinition>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(x => x["FileStorage:Path"]).Returns("/tmp/recall-wiring-test-storage");

        var (queryService, commandService, historyBuilder, attachmentService) = ConversationTestServices.Create(
            scopeFactory, contextOptions.Object, Mock.Of<IMarkdownExtractionService>(), configuration.Object);
        var (persistence, usageReporter) = ConversationTestServices.CreatePersistence(scopeFactory);

        return ConversationTestServices.CreateConversationService(
            scopeFactory,
            chatModelResolver.Object,
            queryService,
            commandService,
            historyBuilder,
            attachmentService,
            persistence,
            usageReporter,
            chatClientFactory.Object,
            lockMock.Object);
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
}
