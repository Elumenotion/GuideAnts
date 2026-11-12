using Microsoft.EntityFrameworkCore;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Tests.TestUtils;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using System.Collections;
using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize]
public class ConversationServiceAssistantSwitchingTests
{
    // AssistantUtility caches definitions in a static, process-wide dictionary and
    // PrepareMessagesForAssistantAsync resolves EVERY assistant name through it. An unseeded name falls
    // through to DatabaseStorage, which reads ConnectionStrings:DefaultConnection from the PROCESS
    // environment and opens real SQL - that is the ~15 s failure this class used to show once another
    // assembly's container connection string was in the environment. This class now seeds the one name
    // its assertions need and never reads or writes the env var, following the precedent in
    // ConversationHistoryBuilderCompactionTests / ConversationServicePreflightTests.
    private const string SwitchAssistantName = "Code and Data";

    private ApplicationDbContext _dbContext = null!;
    private ConversationService _service = null!;
    private ConversationHistoryBuilder _historyBuilder = null!;
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
    private Mock<IChatCompletionClientFactory> _mockChatClientFactory = null!;
    private ClaimsPrincipal _testUser = null!;
    private Guid _userId;
    private Guid _projectId;
    private Guid _notebookId;
    private Guid _conversationId;
    private Guid _templateId;

    [TestInitialize]
    public void TestInitialize()
    {
        // Create in-memory database
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        // Setup mocks
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        _mockChatClientFactory = new Mock<IChatCompletionClientFactory>();

        // Setup test data
        _userId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _conversationId = Guid.NewGuid();
        _templateId = Guid.NewGuid();

        _testUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim("access_token", "test-token")
        }));

        // Setup access service mock

        // Create service
        var scopeFactory = new GuideAntsApi.Tests.TestUtils.TestServiceScopeFactory(_dbContext);
        var distributedLockMock = new Mock<IDistributedConversationLock>();
        var usageRecorderMock = new Mock<IUsageRecorder>();
        var contextOptionsServiceMock = new Mock<IContextOptionsService>();
        contextOptionsServiceMock
            .Setup(m => m.BuildContextMessageAsync(It.IsAny<AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // Configuration needed by ConversationService
        var configurationMock = new Mock<IConfiguration>();
        configurationMock.Setup(x => x["FileStorage:Path"]).Returns("C:\\temp\\test-storage");

        var chatModelResolverMock = new Mock<IChatModelResolver>();
        chatModelResolverMock
            .Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns((string? id) => new ResolvedChatModel(
                string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var (queryService, commandService, historyBuilder, attachmentService) = ConversationTestServices.Create(
            scopeFactory,
            contextOptionsServiceMock.Object,
            Mock.Of<IMarkdownExtractionService>(),
            configurationMock.Object);
        var (persistence, usageReporter) = ConversationTestServices.CreatePersistence(scopeFactory, usageRecorderMock.Object);
        _historyBuilder = historyBuilder;

        _service = ConversationTestServices.CreateConversationService(
            scopeFactory,
            chatModelResolverMock.Object,
            queryService,
            commandService,
            historyBuilder,
            attachmentService,
            persistence,
            usageReporter,
            _mockChatClientFactory.Object,
            distributedLockMock.Object);

        // Seed test data, then seed the static assistant cache so no assistant lookup can reach SQL.
        // ClearAllCache first: another suite may have left an entry for this name behind.
        SeedTestData();
        AssistantUtility.ClearAllCache();
        // Seeded with no Instructions on purpose: these tests assert the conversation tail only, and a
        // non-empty Instructions would prepend a system message and change every expected count.
        SeedAssistantCache("assistant");
        SeedAssistantCache("python-assistant");
        // Seeded WITH instructions so the assistant-switch branch is the branch under test.
        SeedAssistantCache(SwitchAssistantName, "You are a Python data analyst. Use pandas.");
    }

    /// <summary>
    /// Seeds AssistantUtility's static cache directly, bypassing the DatabaseStorage lookup
    /// PrepareMessagesForAssistantAsync would otherwise perform. Mirrors
    /// ConversationHistoryBuilderCompactionTests.SeedAssistantCache.
    /// </summary>
    private static void SeedAssistantCache(string assistantName, string? instructions = null)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, new AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition
        {
            Name = assistantName,
            Instructions = instructions
        })!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[assistantName] = entry;
    }

    private void SeedTestData()
    {
        var template = new NotebookTemplate
        {
            Id = _templateId,
            TemplateName = "test-template"
        };

        var notebook = new Notebook
        {
            Id = _notebookId,
            ProjectId = _projectId,
            NotebookTemplateId = _templateId,
            Title = "Test Notebook"
        };

        var conversation = new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "Test Conversation",
            Notebook = notebook
        };

        _dbContext.NotebookTemplates.Add(template);
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(conversation);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        AssistantUtility.ClearAllCache();

        _dbContext.Dispose();
    }

    #region New Conversation Tests

    [TestMethod]
    public async Task PrepareMessagesForAssistant_NewConversation_ReturnsEmptyMessages()
    {
        // Arrange
        var conversation = await _dbContext.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .FirstAsync(c => c.Id == _conversationId);

        // Act
        var result = await _historyBuilder.PrepareMessagesForAssistantAsync(
            conversation,
            "assistant",
            _userId,
            CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PrepareMessagesForAssistant_NewConversationWithDifferentAssistant_ReturnsEmptyMessages()
    {
        // Arrange
        var conversation = await _dbContext.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .FirstAsync(c => c.Id == _conversationId);

        // Act
        var result = await _historyBuilder.PrepareMessagesForAssistantAsync(
            conversation,
            "python-assistant",
            _userId,
            CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region Same Assistant Continuation Tests

    [TestMethod]
    public async Task PrepareMessagesForAssistant_SameAssistantContinuation_ReturnsAllMessages()
    {
        // Arrange
        var conversation = await SetupConversationWithMessages("assistant");

        // Act
        var result = await _historyBuilder.PrepareMessagesForAssistantAsync(
            conversation,
            "assistant",
            _userId,
            CancellationToken.None);

        // Assert
        result.Should().HaveCount(3); // User, Assistant, User messages (excluding tool message)
        result[0].Role.Should().Be(ChatMessageRole.User);
        result[1].Role.Should().Be(ChatMessageRole.Assistant);
        result[2].Role.Should().Be(ChatMessageRole.User);
    }

    #endregion

    #region Assistant Switching Tests

    [TestMethod]
    public async Task PrepareMessagesForAssistant_AssistantSwitch_FiltersToolCallsAndAddsSystemMessages()
    {
        // Arrange
        var conversation = await SetupConversationWithMessagesAndToolCalls("assistant");

        // "Code and Data" is seeded into AssistantUtility's static cache by TestInitialize, so the
        // switch branch is deterministic and cannot fall back to the null-definition path (which used
        // to make every assertion below unreachable).

        // Act
        var result = await _historyBuilder.PrepareMessagesForAssistantAsync(
            conversation,
            SwitchAssistantName,
            _userId,
            CancellationToken.None);

        // Assert
        // Should apply assistant switching logic with system messages and filtered conversation messages
        result.Should().HaveCount(4); // 1 system message + 2 filtered conversation messages + 1 handoff context message
        
        // Check initial system message for new assistant
        result[0].Role.Should().Be(ChatMessageRole.System);
        result[0].GetText().Should().Contain("Python"); // Assistant instructions
        
        // Check filtered conversation messages (should exclude assistant message with tool calls)
        result[1].Role.Should().Be(ChatMessageRole.User);     // Hello
        result[2].Role.Should().Be(ChatMessageRole.User);     // Follow up question
        
        // Check handoff context message appears AFTER previous conversation
        result[3].Role.Should().Be(ChatMessageRole.System);
        result[3].GetText().Should().Contain("previous messages between the user and assistant");
    }

    [TestMethod]
    public async Task PrepareMessagesForAssistant_AssistantSwitchWithUnknownAssistant_ReturnsBaseMessages()
    {
        // Arrange
        var conversation = await SetupConversationWithMessages("assistant");

        // This is the one test that must exercise a real cache MISS, so it cannot be served from the
        // seeded static cache. A miss falls through to DatabaseStorage, which reads
        // ConnectionStrings:DefaultConnection from the process environment; with no value set it throws
        // InvalidOperationException internally and returns null immediately (no SQL attempt), which is the
        // outcome asserted here. Guard the variable for just this call and restore it in finally so the
        // class never leaves a value - or a stale snapshot of one - behind.
        const string connectionStringKey = "ConnectionStrings:DefaultConnection";
        const string connectionStringKeyDoubleUnderscore = "ConnectionStrings__DefaultConnection";
        var priorConnectionString = Environment.GetEnvironmentVariable(connectionStringKey);
        var priorConnectionStringDoubleUnderscore = Environment.GetEnvironmentVariable(connectionStringKeyDoubleUnderscore);
        Environment.SetEnvironmentVariable(connectionStringKey, null);
        Environment.SetEnvironmentVariable(connectionStringKeyDoubleUnderscore, null);

        try
        {
            // Act
            var result = await _historyBuilder.PrepareMessagesForAssistantAsync(
                conversation,
                "definitely-does-not-exist-assistant",
                _userId,
                CancellationToken.None);

            // Assert
            result.Should().HaveCount(4); // Original messages without filtering since assistant definition not found
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionStringKey, priorConnectionString);
            Environment.SetEnvironmentVariable(connectionStringKeyDoubleUnderscore, priorConnectionStringDoubleUnderscore);
        }
    }

    #endregion

    #region Helper Methods

    private async Task<NotebookConversation> SetupConversationWithMessages(string assistantName)
    {
        var conversation = await _dbContext.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .Include(c => c.Notebook)
            .ThenInclude(n => n.Guide)
            .FirstAsync(c => c.Id == _conversationId);

        // Add a turn
        var turn = new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = assistantName,
            Instructions = "Hello",
            Created = DateTime.UtcNow
        };
        _dbContext.ConversationTurns.Add(turn);

        // Add messages
        var messages = new[]
        {
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "Hello",
                AssistantName = "user",
                Created = DateTime.UtcNow
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "Hi there!",
                AssistantName = assistantName,
                Created = DateTime.UtcNow
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 3,
                Role = DataModelChatRole.Tool,
                Content = "Tool result",
                AssistantName = assistantName,
                ToolCallId = "tool_123",
                FunctionName = "test_function",
                Created = DateTime.UtcNow
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 4,
                Role = DataModelChatRole.User,
                Content = "Follow up question",
                AssistantName = "user",
                Created = DateTime.UtcNow
            }
        };

        _dbContext.NotebookConversationMessages.AddRange(messages);
        await _dbContext.SaveChangesAsync();

        return conversation;
    }

    private async Task<NotebookConversation> SetupConversationWithMessagesAndToolCalls(string assistantName)
    {
        var conversation = await _dbContext.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .Include(c => c.Notebook)
            .ThenInclude(n => n.Guide)
            .FirstAsync(c => c.Id == _conversationId);

        // Add a turn
        var turn = new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = assistantName,
            Instructions = "Hello",
            Created = DateTime.UtcNow
        };
        _dbContext.ConversationTurns.Add(turn);

        // Add messages including assistant message with tool calls
        var toolCallsJson = """[{"id":"call_123","type":"function","function":{"name":"test_function","arguments":"{}"}}]""";
        
        var messages = new[]
        {
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "Hello",
                AssistantName = "user",
                Created = DateTime.UtcNow
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "I'll help you with that.",
                AssistantName = assistantName,
                ToolCalls = toolCallsJson, // This message has tool calls
                Created = DateTime.UtcNow
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 3,
                Role = DataModelChatRole.User,
                Content = "Follow up question",
                AssistantName = "user",
                Created = DateTime.UtcNow
            }
        };

        _dbContext.NotebookConversationMessages.AddRange(messages);
        await _dbContext.SaveChangesAsync();

        return conversation;
    }

    #endregion
}
