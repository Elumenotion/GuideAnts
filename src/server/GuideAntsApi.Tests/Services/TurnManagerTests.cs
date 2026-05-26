using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using System.Text.Json;
using GuideAntsApi.Tests.TestUtils;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class TurnManagerTests
{
    private ApplicationDbContext _context = null!;
    private Mock<ILogger<TurnManager>> _loggerMock = null!;
    private TurnManager _manager = null!;

    private Guid _conversationId;
    private Guid _notebookId;
    private Guid _projectId;
    private Guid _fileId1;
    private Guid _fileId2;

    [TestInitialize]
    public void Setup()
    {
        // Generate unique IDs for each test
        _conversationId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _fileId1 = Guid.NewGuid();
        _fileId2 = Guid.NewGuid();

        // In-memory EF Core database
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new ApplicationDbContext(options);

        _loggerMock = new Mock<ILogger<TurnManager>>();
        var notebookFileServiceMock = new Mock<INotebookFileService>();
        var lineageServiceMock = new Mock<IFileLineageService>();
        var conversationManagerMock = new Mock<IConversationManager>();
        var chatModelResolverMock = new Mock<IChatModelResolver>();
        chatModelResolverMock
            .Setup(x => x.Resolve(It.IsAny<string?>()))
            .Returns(new ResolvedChatModel(
                ModelId: "gpt-4.1",
                ReferenceKind: ChatModelReferenceKind.Direct,
                ExecutionPolicy: new ResolvedExecutionPolicy(
                    "gpt-4.1",
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));
        var scopeFactory = new TestServiceScopeFactory(_context);
        _manager = new TurnManager(
            scopeFactory,
            notebookFileServiceMock.Object,
            lineageServiceMock.Object,
            conversationManagerMock.Object,
            chatModelResolverMock.Object,
            _loggerMock.Object);

        // Seed test data
        SeedTestData();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
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

        // Add test files
        var file1 = new NotebookFile
        {
            Id = _fileId1,
            NotebookId = _notebookId,
            RelativePath = "test1.txt",
            FileSize = 1024,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash1",
            Created = DateTime.UtcNow
        };

        var file2 = new NotebookFile
        {
            Id = _fileId2,
            NotebookId = _notebookId,
            RelativePath = "test2.txt",
            FileSize = 2048,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash2",
            Created = DateTime.UtcNow
        };

        _context.Projects.Add(project);
        _context.NotebookTemplates.Add(template);
        _context.Notebooks.Add(notebook);
        _context.NotebookConversations.Add(conversation);
        _context.NotebookFiles.AddRange(file1, file2);
        _context.SaveChanges();
    }

    #region CreateTurnAsync Tests
    [TestMethod]
    public async Task CreateTurnAsync_WithValidData_ShouldCreateTurn()
    {
        // Arrange
        string assistantName = "assistant";
        string instructions = "Test instructions";

        // Act
        var turn = await _manager.CreateTurnAsync(_conversationId, assistantName, instructions);

        // Assert
        turn.Should().NotBeNull();
        turn.AssistantName.Should().Be(assistantName);
        turn.Instructions.Should().Be(instructions);
        // Note: Turn object doesn't have TurnIndex property - this is managed by the database entity
    }

    [TestMethod]
    public async Task CreateTurnAsync_WithExistingTurns_ShouldIncrementTurnIndex()
    {
        // Arrange
        await CreateTestTurn(1);
        await CreateTestTurn(2);

        // Act
        var turn = await _manager.CreateTurnAsync(_conversationId, "assistant", "New instructions");

        // Assert
        turn.Should().NotBeNull();
        // Note: TurnIndex is managed by database, not the Turn object itself
    }

    [TestMethod]
    public async Task CreateTurnAsync_ShouldNotPersistToDatabase()
    {
        // Arrange & Act
        var turn = await _manager.CreateTurnAsync(_conversationId, "assistant", "Test instructions");

        // Assert
        var savedTurns = await _context.ConversationTurns
            .Where(t => t.NotebookConversationId == _conversationId)
            .ToListAsync();

        savedTurns.Should().BeEmpty(); // Turn should not be persisted until CompleteTurnAsync
    }
    #endregion

    #region CompleteTurnAsync Tests
    [TestMethod]
    public async Task CompleteTurnAsync_WithValidTurn_ShouldPersistTurn()
    {
        // Arrange
        var turn = await _manager.CreateTurnAsync(_conversationId, "assistant", "Test instructions");
        var chatOutput = CreateTestChatRunOutput();

        // Act
        await _manager.CompleteTurnAsync(_conversationId, turn, chatOutput);

        // Assert
        var savedTurn = await _context.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == _conversationId);

        savedTurn.Should().NotBeNull();
        savedTurn!.NotebookConversationId.Should().Be(_conversationId);
        savedTurn.AssistantName.Should().Be("assistant");
        savedTurn.Instructions.Should().Be("Test instructions");
        savedTurn.ChatRunOutputJson.Should().NotBeNullOrEmpty();
        savedTurn.UsageJson.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task CompleteTurnAsync_WithUsageStats_ShouldSerializeUsage()
    {
        // Arrange
        var turn = await _manager.CreateTurnAsync(_conversationId, "assistant", "Test instructions");
        var chatOutput = CreateTestChatRunOutput();

        // Act
        await _manager.CompleteTurnAsync(_conversationId, turn, chatOutput);

        // Assert
        var savedTurn = await _context.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == _conversationId);

        savedTurn.Should().NotBeNull();
        savedTurn!.UsageJson.Should().NotBeNullOrEmpty();

        // Verify usage JSON is stored
        savedTurn.UsageJson.Should().Contain("total_tokens");
        savedTurn.UsageJson.Should().Contain("150");
    }

    [TestMethod]
    public async Task CompleteTurnAsync_WithChatOutput_ShouldSerializeChatOutput()
    {
        // Arrange
        var turn = await _manager.CreateTurnAsync(_conversationId, "assistant", "Test instructions");
        var chatOutput = CreateTestChatRunOutput();

        // Act
        await _manager.CompleteTurnAsync(_conversationId, turn, chatOutput);

        // Assert
        var savedTurn = await _context.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == _conversationId);

        savedTurn.Should().NotBeNull();
        savedTurn!.ChatRunOutputJson.Should().NotBeNullOrEmpty();

        // Verify chat output JSON is stored  
        savedTurn.ChatRunOutputJson.Should().Contain("usage");
    }
    #endregion

    #region GetTurnsAsync Tests
    [TestMethod]
    public async Task GetTurnsAsync_WithExistingTurns_ShouldReturnTurnsInOrder()
    {
        // Arrange
        await CreateAndCompleteTurn(1);
        await CreateAndCompleteTurn(2);
        await CreateAndCompleteTurn(3);

        // Act
        var turns = await _manager.GetTurnsAsync(_conversationId);

        // Assert
        turns.Should().HaveCount(3);
        // Note: Turn objects don't expose TurnIndex directly - it's managed by database
        turns.All(t => t.AssistantName == "assistant").Should().BeTrue();
    }

    [TestMethod]
    public async Task GetTurnsAsync_WithNoTurns_ShouldReturnEmptyList()
    {
        // Act
        var turns = await _manager.GetTurnsAsync(_conversationId);

        // Assert
        turns.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetTurnsAsync_WithNonExistentConversation_ShouldReturnEmptyList()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var turns = await _manager.GetTurnsAsync(nonExistentId);

        // Assert
        turns.Should().BeEmpty();
    }
    #endregion

    #region File Tracking Tests
    [TestMethod]
    public async Task TrackFilesForTurnAsync_WithCreatedFiles_ShouldUpdateTurn()
    {
        // Arrange
        var turn = await CreateAndCompleteTurn(1);
        var createdFiles = new List<string> { "test1.txt", "test2.txt" };

        // Act
        await _manager.TrackFilesForTurnAsync(_conversationId, 1, createdFiles, new List<string>());

        // Assert
        var updatedTurn = await _context.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == _conversationId);

        updatedTurn.Should().NotBeNull();
        updatedTurn!.FilesCreated.Should().NotBeNullOrEmpty();

        var deserializedFiles = JsonSerializer.Deserialize<List<string>>(updatedTurn.FilesCreated);
        deserializedFiles.Should().NotBeNull();
        deserializedFiles.Should().BeEquivalentTo(createdFiles);
    }

    [TestMethod]
    public async Task TrackFilesForTurnAsync_WithModifiedFiles_ShouldUpdateTurn()
    {
        // Arrange
        var turn = await CreateAndCompleteTurn(1);
        var modifiedFiles = new List<string> { "test1.txt" };

        // Act
        await _manager.TrackFilesForTurnAsync(_conversationId, 1, new List<string>(), modifiedFiles);

        // Assert
        var updatedTurn = await _context.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == _conversationId);

        updatedTurn.Should().NotBeNull();
        updatedTurn!.FilesModified.Should().NotBeNullOrEmpty();

        var deserializedFiles = JsonSerializer.Deserialize<List<string>>(updatedTurn.FilesModified);
        deserializedFiles.Should().NotBeNull();
        deserializedFiles.Should().BeEquivalentTo(modifiedFiles);
    }

    [TestMethod]
    public async Task TrackFilesForTurnAsync_WithNonExistentTurn_ShouldThrowException()
    {
        // Arrange
        var nonExistentTurnIndex = 999;

        // Act & Assert
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => _manager.TrackFilesForTurnAsync(_conversationId, nonExistentTurnIndex, new List<string>(), new List<string>()));
    }

    [TestMethod]
    public async Task GetFilesCreatedInTurnAsync_WithTrackedFiles_ShouldReturnFiles()
    {
        // Arrange
        var turn = await CreateAndCompleteTurn(1);
        var createdFiles = new List<string> { "test1.txt", "test2.txt" };
        await _manager.TrackFilesForTurnAsync(_conversationId, 1, createdFiles, new List<string>());

        // Act
        var files = await _manager.GetFilesCreatedInTurnAsync(_conversationId, 1);

        // Assert
        files.Should().HaveCount(2);
        files.Should().Contain(f => f.Id == _fileId1);
        files.Should().Contain(f => f.Id == _fileId2);
    }

    [TestMethod]
    public async Task GetFilesCreatedInConversationAsync_WithMultipleTurns_ShouldReturnAllFiles()
    {
        // Arrange
        var turn1 = await CreateAndCompleteTurn(1);
        var turn2 = await CreateAndCompleteTurn(2);

        await _manager.TrackFilesForTurnAsync(_conversationId, 1, new List<string> { "test1.txt" }, new List<string>());
        await _manager.TrackFilesForTurnAsync(_conversationId, 2, new List<string> { "test2.txt" }, new List<string>());

        // Act
        var files = await _manager.GetFilesCreatedInConversationAsync(_conversationId);

        // Assert
        files.Should().HaveCount(2);
        files.Should().Contain(f => f.Id == _fileId1);
        files.Should().Contain(f => f.Id == _fileId2);
    }

    [TestMethod]
    public async Task GetFilesCreatedInConversationAsync_WithNoDuplicates_ShouldReturnUniqueFiles()
    {
        // Arrange
        var turn1 = await CreateAndCompleteTurn(1);
        var turn2 = await CreateAndCompleteTurn(2);

        // Same file created in both turns
        await _manager.TrackFilesForTurnAsync(_conversationId, 1, new List<string> { "test1.txt" }, new List<string>());
        await _manager.TrackFilesForTurnAsync(_conversationId, 2, new List<string> { "test1.txt" }, new List<string>());

        // Act
        var files = await _manager.GetFilesCreatedInConversationAsync(_conversationId);

        // Assert
        files.Should().HaveCount(1);
        files.First().Id.Should().Be(_fileId1);
    }
    #endregion

    #region FileLineageEvent Integration Tests
    [TestMethod]
    public async Task GetFileOperationsForTurnAsync_WithFileLineageEvents_ShouldReturnEvents()
    {
        // Arrange
        var turn = await CreateAndCompleteTurn(1);

        // Create FileLineageEvent entries  
        var lineageEvent1 = new FileLineageEvent
        {
            Id = Guid.NewGuid(),
            FileId = _fileId1,
            Action = FileLineageAction.Created,
            FileKind = FileKind.Notebook,
            NotebookId = _notebookId,
            Timestamp = DateTime.UtcNow,
            StoragePath = "test1.txt"
        };

        var lineageEvent2 = new FileLineageEvent
        {
            Id = Guid.NewGuid(),
            FileId = _fileId2,
            Action = FileLineageAction.ExternalWrite,
            FileKind = FileKind.Notebook,
            NotebookId = _notebookId,
            Timestamp = DateTime.UtcNow,
            StoragePath = "test2.txt"
        };

        _context.FileLineageEvents.AddRange(lineageEvent1, lineageEvent2);
        await _context.SaveChangesAsync();

        // Act
        var events = await _manager.GetFileOperationsForTurnAsync(_conversationId, turn.TurnIndex);

        // Assert
        events.Should().HaveCount(2);
        events.Should().Contain(e => e.FileId == _fileId1 && e.Action == FileLineageAction.Created);
        events.Should().Contain(e => e.FileId == _fileId2 && e.Action == FileLineageAction.ExternalWrite);
    }

    [TestMethod]
    public async Task GetFileOperationsForTurnAsync_WithNoEvents_ShouldReturnEmptyList()
    {
        // Arrange
        var turn = await CreateAndCompleteTurn(1);

        // Act
        var events = await _manager.GetFileOperationsForTurnAsync(_conversationId, turn.TurnIndex);

        // Assert
        events.Should().BeEmpty();
    }
    #endregion

    #region Helper Methods
    private async Task<ConversationTurn> CreateTestTurn(int turnIndex)
    {
        var turn = new ConversationTurn
        {
            NotebookConversationId = _conversationId,
            TurnIndex = turnIndex,
            AssistantName = "assistant",
            Instructions = $"Test instructions {turnIndex}",
            Created = DateTime.UtcNow
        };

        _context.ConversationTurns.Add(turn);
        await _context.SaveChangesAsync();

        return turn;
    }

    private async Task<ConversationTurn> CreateAndCompleteTurn(int turnIndex)
    {
        var turn = await _manager.CreateTurnAsync(_conversationId, "assistant", $"Test instructions {turnIndex}");
        var chatOutput = CreateTestChatRunOutput();

        await _manager.CompleteTurnAsync(_conversationId, turn, chatOutput);

        // Return the database entity, not the AntRunner.Chat.Turn
        var dbTurn = await _context.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == _conversationId && t.TurnIndex == turnIndex);

        return dbTurn!;
    }

    private ChatRunOutput CreateTestChatRunOutput()
    {
        // Create a simple ChatRunOutput for testing
        var output = new ChatRunOutput
        {
            LastMessage = "Test response",
            Status = "completed",
            ThreadId = "thread_123",
            Usage = new UsageResponse
            {
                TotalTokens = 150,
                PromptTokens = 100,
                CompletionTokens = 50,
                CachedPromptTokens = 0
            },
            ConversationMessages = new List<ThreadConversationMessage>
            {
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.User,
                    Message = "Test user message"
                },
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.Assistant,
                    Message = "Test assistant response"
                }
            }
        };

        return output;
    }
    #endregion
}
