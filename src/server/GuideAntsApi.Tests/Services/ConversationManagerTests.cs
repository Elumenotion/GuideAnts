using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using FluentAssertions;
using AntRunner.Chat;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize]
public class ConversationManagerTests
{
    private ApplicationDbContext _context = null!;
    private IMemoryCache _cache = null!;
    private Mock<ILogger<ConversationManager>> _loggerMock = null!;
    private ConversationManager _manager = null!;
    private string? _originalConnectionString;

    private Guid _conversationId;
    private Guid _notebookId;
    private Guid _projectId;

    [TestInitialize]
    public void Setup()
    {
        const string connectionStringKey = "ConnectionStrings:DefaultConnection";
        _originalConnectionString = Environment.GetEnvironmentVariable(connectionStringKey);
        Environment.SetEnvironmentVariable(connectionStringKey, null);
        AssistantUtility.ClearAllCache();

        // Generate unique IDs for each test
        _conversationId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _projectId = Guid.NewGuid();

        // In-memory EF Core database
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new ApplicationDbContext(options);

        // Real memory cache for testing caching behavior
        _cache = new MemoryCache(new MemoryCacheOptions());

        _loggerMock = new Mock<ILogger<ConversationManager>>();

        var scopeFactory = new TestServiceScopeFactory(_context);
        var chatModelResolverMock = new Mock<IChatModelResolver>();
        chatModelResolverMock
            .Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns((string? id) => new ResolvedChatModel(
                string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                ChatModelReferenceKind.Direct,
                new AntRunner.Chat.Abstractions.ResolvedExecutionPolicy(
                    string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                    "openai-chat",
                    AntRunner.Chat.Abstractions.ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));
        _manager = new ConversationManager(scopeFactory, _cache, _loggerMock.Object, chatModelResolverMock.Object);

        // Seed test data
        SeedTestData();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings:DefaultConnection", _originalConnectionString);
        AssistantUtility.ClearAllCache();
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _cache.Dispose();
    }

    private void SeedTestData()
    {
        var project = new Project { Id = _projectId, Title = "Test Project" };
        var template = new NotebookTemplate { Id = Guid.NewGuid(), TemplateName = "Test Template" };
        var notebook = new Notebook 
        { 
            Id = _notebookId, 
            Title = "Test Notebook", 
            ProjectId = _projectId,
            NotebookTemplateId = template.Id
        };
        var conversation = new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "Test Conversation"
        };

        _context.Projects.Add(project);
        _context.NotebookTemplates.Add(template);
        _context.Notebooks.Add(notebook);
        _context.NotebookConversations.Add(conversation);
        _context.SaveChanges();
    }

    #region CreateConversationAsync Tests
    [TestMethod]
    public async Task CreateConversationAsync_WithoutDatabaseAssistantDefinition_ShouldThrowException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _manager.CreateConversationAsync(_conversationId, "assistant"));
    }

    [TestMethod]
    public async Task CreateConversationAsync_WithInvalidAssistant_ShouldThrowException()
    {
        // Arrange
        string invalidAssistantName = "non-existent-assistant";

        // Act & Assert
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _manager.CreateConversationAsync(_conversationId, invalidAssistantName));
    }
    #endregion

    #region LoadConversationAsync Tests
    [TestMethod]
    public async Task LoadConversationAsync_WithNonExistentConversation_ShouldThrowException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => _manager.LoadConversationAsync(nonExistentId));
    }
    #endregion

    #region GetCurrentStateAsync Tests
    [TestMethod]
    public async Task GetCurrentStateAsync_WithNonExistentConversation_ShouldThrowException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => _manager.GetCurrentStateAsync(nonExistentId));
    }


    #endregion

    #region GetCurrentAssistantAsync Tests
    [TestMethod]
    public async Task GetCurrentAssistantAsync_WithTurns_ShouldReturnLastTurnAssistant()
    {
        // Arrange
        await AddTestMessagesAndTurns();

        // Act
        var result = await _manager.GetCurrentAssistantAsync(_conversationId);

        // Assert
        result.Should().Be("assistant");
    }

    [TestMethod]
    public async Task GetCurrentAssistantAsync_WithNoTurns_ShouldReturnDefaultAssistant()
    {
        // Act
        var result = await _manager.GetCurrentAssistantAsync(_conversationId);

        // Assert
        result.Should().Be("assistant"); // Default when no turns exist
    }

    [TestMethod]
    public async Task GetCurrentAssistantAsync_ShouldUseCaching()
    {
        // Arrange
        await AddTestMessagesAndTurns();

        // Act - First call
        var result1 = await _manager.GetCurrentAssistantAsync(_conversationId);
        
        // Act - Second call should use cache
        var result2 = await _manager.GetCurrentAssistantAsync(_conversationId);

        // Assert
        result1.Should().Be(result2);
        
        // Verify cache contains the result
        var cacheKey = $"conversation:{_conversationId}:current-assistant";
        _cache.TryGetValue(cacheKey, out var cachedValue).Should().BeTrue();
    }
    #endregion

    #region GetCurrentModelAsync Tests
    [TestMethod]
    public async Task GetCurrentModelAsync_WithTurns_ShouldReturnLastTurnModel()
    {
        // Arrange
        await AddTestMessagesAndTurns();

        // Act
        var result = await _manager.GetCurrentModelAsync(_conversationId);

        // Assert
        result.Should().Be("gpt-4o-mini"); // From test turn data
    }

    [TestMethod]
    public async Task GetCurrentModelAsync_WithNoTurns_ShouldReturnEmpty()
    {
        // Act
        var result = await _manager.GetCurrentModelAsync(_conversationId);

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCurrentStateAsync_WithLatestTurnAndFileLists_ShouldReturnComputedState()
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-2);
        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 2,
            AssistantName = "planner",
            ModelDeploymentId = "gpt-4.1",
            Instructions = "Build plan",
            FilesCreated = "[\"docs/plan.md\",\"src/main.cs\"]",
            FilesModified = "[\"README.md\"]",
            Created = createdAt
        });
        await _context.SaveChangesAsync();

        var state = await _manager.GetCurrentStateAsync(_conversationId);

        state.ConversationId.Should().Be(_conversationId);
        state.CurrentAssistantName.Should().Be("planner");
        state.CurrentModelDeploymentId.Should().Be("gpt-4.1");
        state.LastInstructions.Should().Be("Build plan");
        state.CurrentTurnIndex.Should().Be(2);
        state.LastActivity.Should().BeCloseTo(createdAt, TimeSpan.FromSeconds(1));
        state.LastTurnFilesCreated.Should().BeEquivalentTo(["docs/plan.md", "src/main.cs"]);
        state.LastTurnFilesModified.Should().BeEquivalentTo(["README.md"]);
    }

    [TestMethod]
    public async Task GetCurrentStateAsync_WithInvalidFileJson_ShouldIgnoreFileLists()
    {
        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = "assistant",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "Test",
            FilesCreated = "{bad-json",
            FilesModified = "[\"ok\",",
            Created = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var state = await _manager.GetCurrentStateAsync(_conversationId);

        state.CurrentTurnIndex.Should().Be(1);
        state.LastTurnFilesCreated.Should().BeNull();
        state.LastTurnFilesModified.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCurrentStateAsync_ShouldReturnCachedStateUntilInvalidated()
    {
        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = "assistant-one",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "First turn",
            Created = DateTime.UtcNow.AddMinutes(-2)
        });
        await _context.SaveChangesAsync();

        var initialState = await _manager.GetCurrentStateAsync(_conversationId);
        initialState.CurrentAssistantName.Should().Be("assistant-one");
        initialState.CurrentTurnIndex.Should().Be(1);

        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 2,
            AssistantName = "assistant-two",
            ModelDeploymentId = "gpt-4.1",
            Instructions = "Second turn",
            Created = DateTime.UtcNow.AddMinutes(-1)
        });
        await _context.SaveChangesAsync();

        var cachedState = await _manager.GetCurrentStateAsync(_conversationId);
        cachedState.CurrentAssistantName.Should().Be("assistant-one");
        cachedState.CurrentTurnIndex.Should().Be(1);

        _manager.InvalidateCache(_conversationId);
        var refreshedState = await _manager.GetCurrentStateAsync(_conversationId);

        refreshedState.CurrentAssistantName.Should().Be("assistant-two");
        refreshedState.CurrentTurnIndex.Should().Be(2);
    }

    [TestMethod]
    public async Task LoadConversationAsync_WithSystemAndUserMessages_Throws_when_assistant_definition_missing()
    {
        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = "Code Executor",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "Seed turn",
            Created = DateTime.UtcNow.AddMinutes(-3)
        });
        _context.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = ChatRole.System,
                Content = "System context",
                Created = DateTime.UtcNow.AddMinutes(-2)
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = ChatRole.User,
                Content = "Hello",
                Created = DateTime.UtcNow.AddMinutes(-1)
            });
        await _context.SaveChangesAsync();

        var act = () => _manager.LoadConversationAsync(_conversationId);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*Can't find assistant definition*");
    }

    [TestMethod]
    public async Task LoadConversationAsync_WithToolMessage_Throws_when_assistant_definition_missing()
    {
        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = "Code Executor",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "Use search tool",
            Created = DateTime.UtcNow.AddMinutes(-2)
        });
        _context.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = ChatRole.User,
                Content = "Find docs",
                Created = DateTime.UtcNow.AddMinutes(-2)
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = ChatRole.Assistant,
                AssistantName = "Code Executor",
                Content = "Calling tool",
                Created = DateTime.UtcNow.AddMinutes(-1)
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = _conversationId,
                TurnIndex = 1,
                MessageSequence = 3,
                Role = ChatRole.Tool,
                FunctionName = "SearchDocs",
                ToolCallId = "call_123",
                Content = "{\"result\":\"ok\"}",
                Created = DateTime.UtcNow
            });
        await _context.SaveChangesAsync();

        var act = () => _manager.LoadConversationAsync(_conversationId);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*Can't find assistant definition*");
    }

    [TestMethod]
    public async Task InvalidateCache_ShouldForceAssistantRefresh()
    {
        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = "assistant-one",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "First",
            Created = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var firstAssistant = await _manager.GetCurrentAssistantAsync(_conversationId);
        firstAssistant.Should().Be("assistant-one");

        _context.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 2,
            AssistantName = "assistant-two",
            ModelDeploymentId = "gpt-4.1",
            Instructions = "Second",
            Created = DateTime.UtcNow.AddSeconds(1)
        });
        await _context.SaveChangesAsync();

        var cachedAssistant = await _manager.GetCurrentAssistantAsync(_conversationId);
        cachedAssistant.Should().Be("assistant-one");

        _manager.InvalidateCache(_conversationId);
        var refreshedAssistant = await _manager.GetCurrentAssistantAsync(_conversationId);

        refreshedAssistant.Should().Be("assistant-two");
    }
    #endregion





    #region Helper Methods
    private async Task AddTestMessagesAndTurns()
    {
        // Add a conversation turn
        var turn = new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            AssistantName = "assistant",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "Test instructions",
            Created = DateTime.UtcNow
        };
        _context.ConversationTurns.Add(turn);

        // Add test messages
        var userMessage = new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            MessageSequence = 1,
            Role = ChatRole.User,
            Content = "Hello",
            Created = DateTime.UtcNow
        };

        var assistantMessage = new NotebookConversationMessage
        {
            NotebookConversationId = _conversationId,
            TurnIndex = 1,
            MessageSequence = 2,
            Role = ChatRole.Assistant,
            Content = "Hello! How can I help you?",
            AssistantName = "assistant",
            Created = DateTime.UtcNow.AddSeconds(1)
        };

        _context.NotebookConversationMessages.AddRange(userMessage, assistantMessage);
        await _context.SaveChangesAsync();
    }
    #endregion
} 
