using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using FluentAssertions;
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

    private Guid _conversationId;
    private Guid _notebookId;
    private Guid _projectId;

    [TestInitialize]
    public void Setup()
    {
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
                null,
                null,
                null));
        _manager = new ConversationManager(scopeFactory, _cache, _loggerMock.Object, chatModelResolverMock.Object);

        // Seed test data
        SeedTestData();
    }

    [TestCleanup]
    public void Cleanup()
    {
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
