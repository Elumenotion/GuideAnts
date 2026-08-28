using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Collections;
using System.Reflection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
[DoNotParallelize]
public sealed class ConversationHistoryBuilderBatchingTests
{
    // AssistantUtility caches assistant definitions in a static, process-wide dictionary (see
    // AssistantUtilityCacheTests.cs for the established seeding pattern this mirrors). ApplyAssistantSwitchLogicAsync
    // resolves the switch-target assistant through that same static cache, so tests exercising it must seed and
    // clear a name distinctive enough not to collide with any other suite's usage - hence [DoNotParallelize] plus
    // this dedicated constant instead of reusing "Claude".
    private const string SwitchAssistantName = "Task4BatchingSwitchTestAssistant";

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private readonly TestServiceScopeFactory _inner;
        public int CreateScopeCount;
        public CountingScopeFactory(TestServiceScopeFactory inner) => _inner = inner;
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref CreateScopeCount);
            return _inner.CreateScope();
        }
    }

    private ApplicationDbContext _dbContext = null!;
    private CountingScopeFactory _countingFactory = null!;
    private ConversationHistoryBuilder _builder = null!;
    private Guid _conversationId;
    private Guid _notebookId;

    [TestInitialize]
    public void TestInitialize()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
        _countingFactory = new CountingScopeFactory(new TestServiceScopeFactory(_dbContext));

        var attachments = new AttachmentContentService(
            _countingFactory,
            Microsoft.Extensions.Options.Options.Create(new GuideAntsApi.Options.MarkdownAttachmentOptions()),
            notebookFileService: null,
            markdownExtractionService: null,
            Mock.Of<ILogger<AttachmentContentService>>(),
            configuration: null);

        _builder = new ConversationHistoryBuilder(
            _countingFactory,
            Mock.Of<IContextOptionsService>(),
            attachments,
            Mock.Of<ILogger<ConversationHistoryBuilder>>());

        SeedConversation();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _dbContext.Dispose();
        AssistantUtility.ClearCache(SwitchAssistantName);
    }

    private void SeedConversation()
    {
        _conversationId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        var notebook = new Notebook { Id = _notebookId, ProjectId = Guid.NewGuid(), Title = "NB" };
        var conversation = new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "Convo",
            Notebook = notebook
        };
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(conversation);

        for (var turn = 1; turn <= 3; turn++)
        {
            _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = _conversationId,
                TurnIndex = turn,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = $"user message {turn}",
                Created = DateTime.UtcNow.AddMinutes(turn)
            });
            _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = _conversationId,
                TurnIndex = turn,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = $"assistant message {turn}",
                AssistantName = "Claude",
                Created = DateTime.UtcNow.AddMinutes(turn).AddSeconds(30)
            });
        }
        _dbContext.SaveChanges();
    }

    private NotebookConversation LoadConversation() =>
        _dbContext.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .Include(c => c.Notebook)
            .AsNoTracking()
            .Single(c => c.Id == _conversationId);

    /// <summary>
    /// Seeds AssistantUtility's static cache directly (bypassing the DB-backed lookup
    /// ApplyAssistantSwitchLogicAsync would otherwise perform) so the assistant-switch branch that carries
    /// the attachment batching can run hermetically. Mirrors AssistantUtilityCacheTests.SeedCache.
    /// </summary>
    private static void SeedAssistantCache(string assistantName)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, new AssistantDefinition { Name = assistantName })!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[assistantName] = entry;
    }

    private NotebookFile NewNotebookFile(string relativePath, string hash)
    {
        var file = new NotebookFile { Id = Guid.NewGuid(), NotebookId = _notebookId, RelativePath = relativePath, FileHash = hash };
        file.GenerateDocumentId(_notebookId);
        return file;
    }

    /// <summary>
    /// Appends one more message to the already-seeded conversation with two attachments, inserted in
    /// reverse OrderIndex order (OrderIndex 1 saved before OrderIndex 0) so a test asserting output order
    /// actually proves ordering comes from OrderIndex, not row-insertion sequence.
    /// </summary>
    private (NotebookFile FileA, NotebookFile FileB) AddAttachmentBearingMessage(
        int turnIndex, DataModelChatRole role, string? assistantName, string content)
    {
        var messageId = Guid.NewGuid();
        _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = messageId,
            NotebookConversationId = _conversationId,
            TurnIndex = turnIndex,
            MessageSequence = 1,
            Role = role,
            Content = content,
            AssistantName = assistantName,
            Created = DateTime.UtcNow.AddMinutes(turnIndex)
        });

        var fileA = NewNotebookFile("a.txt", "hA");
        var fileB = NewNotebookFile("b.txt", "hB");
        _dbContext.NotebookFiles.AddRange(fileA, fileB);

        _dbContext.MessageAttachments.Add(new MessageAttachment
        {
            Id = Guid.NewGuid(), MessageId = messageId, NotebookFileId = fileB.Id, OrderIndex = 1, Created = DateTime.UtcNow
        });
        _dbContext.MessageAttachments.Add(new MessageAttachment
        {
            Id = Guid.NewGuid(), MessageId = messageId, NotebookFileId = fileA.Id, OrderIndex = 0, Created = DateTime.UtcNow
        });

        _dbContext.SaveChanges();
        return (fileA, fileB);
    }

    /// <summary>
    /// Appends an orphan tool-call message (no assistant message declares this ToolCallId, so both
    /// BuildOpenAiMessagesAsync and ApplyAssistantSwitchLogicAsync filter it via `continue`) that also
    /// carries an attachment, proving a filtered message's attachment never leaks into the batch lookup's
    /// output even though the batch query fetches attachments for every message id up front.
    /// </summary>
    private void AddOrphanToolMessageWithAttachment(int turnIndex, string toolCallId)
    {
        var messageId = Guid.NewGuid();
        _dbContext.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = messageId,
            NotebookConversationId = _conversationId,
            TurnIndex = turnIndex,
            MessageSequence = 1,
            Role = DataModelChatRole.Tool,
            ToolCallId = toolCallId,
            FunctionName = "whatever",
            Content = "tool output",
            Created = DateTime.UtcNow.AddMinutes(turnIndex)
        });

        var fileC = NewNotebookFile("c.txt", "hC");
        _dbContext.NotebookFiles.Add(fileC);
        _dbContext.MessageAttachments.Add(new MessageAttachment
        {
            Id = Guid.NewGuid(), MessageId = messageId, NotebookFileId = fileC.Id, OrderIndex = 0, Created = DateTime.UtcNow
        });

        _dbContext.SaveChanges();
    }

    private static Mock<IAttachmentContentService> CreateStrictAttachmentMockForFiles(NotebookFile fileA, NotebookFile fileB)
    {
        var mock = new Mock<IAttachmentContentService>(MockBehavior.Strict);
        mock.Setup(s => s.CreateOpenAiContentFromLoadedFileAsync(
                It.Is<NotebookFile>(nf => nf.Id == fileA.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatContent> { new("FILE_A") });
        mock.Setup(s => s.CreateOpenAiContentFromLoadedFileAsync(
                It.Is<NotebookFile>(nf => nf.Id == fileB.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatContent> { new("FILE_B") });
        // CreateOpenAiContentFromNotebookFileAsync(Guid, ...) is intentionally left unstubbed: this strict
        // mock throws if the id-based fallback is ever invoked, proving the loaded-file fast path is taken.
        return mock;
    }

    [TestMethod]
    public async Task BuildOpenAiMessages_UsesExactlyOneScopeForAttachments_RegardlessOfMessageCount()
    {
        var conv = LoadConversation();
        _countingFactory.CreateScopeCount = 0;

        await _builder.BuildOpenAiMessagesAsync(conv, "Claude");

        _countingFactory.CreateScopeCount.Should().Be(1);
    }

    [TestMethod]
    public async Task BuildOpenAiMessages_ProducesSameMessageListShape()
    {
        var conv = LoadConversation();

        var messages = await _builder.BuildOpenAiMessagesAsync(conv, "Claude");

        messages.Should().HaveCount(6);
        messages.Select(m => m.GetText()).Should().ContainInOrder(
            "user message 1", "assistant message 1",
            "user message 2", "assistant message 2",
            "user message 3", "assistant message 3");
    }

    [TestMethod]
    public async Task ApplyAssistantSwitchLogic_UsesExactlyOneScopeForAttachments_RegardlessOfMessageCount()
    {
        SeedAssistantCache(SwitchAssistantName);
        var conv = LoadConversation();
        _countingFactory.CreateScopeCount = 0;

        await _builder.ApplyAssistantSwitchLogicAsync(conv, SwitchAssistantName);

        _countingFactory.CreateScopeCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ApplyAssistantSwitchLogic_ProducesSameMessageListShape()
    {
        SeedAssistantCache(SwitchAssistantName);
        var conv = LoadConversation();

        var messages = await _builder.ApplyAssistantSwitchLogicAsync(conv, SwitchAssistantName);

        // 6 carried-over conversation messages (no attachments -> plain ToChatMessage path) plus the
        // handoff system message ApplyAssistantSwitchLogicAsync appends whenever the conversation had messages.
        messages.Should().HaveCount(7);
        messages.Take(6).Select(m => m.GetText()).Should().ContainInOrder(
            "user message 1", "assistant message 1",
            "user message 2", "assistant message 2",
            "user message 3", "assistant message 3");
        messages[6].Role.Should().Be(ChatMessageRole.System);
        messages[6].GetText().Should().Contain("previous messages between the user and assistant");
    }

    [TestMethod]
    public async Task BuildOpenAiMessages_PreservesMultiAttachmentOrderingAndUsesLoadedFileFastPath()
    {
        var (fileA, fileB) = AddAttachmentBearingMessage(
            turnIndex: 4, role: DataModelChatRole.Assistant, assistantName: "Claude", content: "assistant text with files");
        AddOrphanToolMessageWithAttachment(turnIndex: 5, toolCallId: "orphan_call_task4");

        var mockAttachments = CreateStrictAttachmentMockForFiles(fileA, fileB);
        var builder = new ConversationHistoryBuilder(
            _countingFactory,
            Mock.Of<IContextOptionsService>(),
            mockAttachments.Object,
            Mock.Of<ILogger<ConversationHistoryBuilder>>());

        var conv = LoadConversation();
        _countingFactory.CreateScopeCount = 0;

        var messages = await builder.BuildOpenAiMessagesAsync(conv, "Claude");

        // One scope for the whole call, even with attachments and a filtered message present.
        _countingFactory.CreateScopeCount.Should().Be(1);

        // 6 baseline messages + the attachment-bearing message; the orphan tool message never surfaces.
        messages.Should().HaveCount(7);
        var attachmentMessage = messages[6];
        attachmentMessage.Content.Select(c => c.Text).Should().ContainInOrder(
            "assistant text with files", "FILE_A", "FILE_B");

        mockAttachments.Verify(
            s => s.CreateOpenAiContentFromLoadedFileAsync(It.IsAny<NotebookFile>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        mockAttachments.Verify(
            s => s.CreateOpenAiContentFromNotebookFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ApplyAssistantSwitchLogic_PreservesMultiAttachmentOrderingAndUsesLoadedFileFastPath()
    {
        SeedAssistantCache(SwitchAssistantName);
        var (fileA, fileB) = AddAttachmentBearingMessage(
            turnIndex: 4, role: DataModelChatRole.Assistant, assistantName: SwitchAssistantName, content: "switch text with files");

        var mockAttachments = CreateStrictAttachmentMockForFiles(fileA, fileB);
        var builder = new ConversationHistoryBuilder(
            _countingFactory,
            Mock.Of<IContextOptionsService>(),
            mockAttachments.Object,
            Mock.Of<ILogger<ConversationHistoryBuilder>>());

        var conv = LoadConversation();
        _countingFactory.CreateScopeCount = 0;

        var messages = await builder.ApplyAssistantSwitchLogicAsync(conv, SwitchAssistantName);

        _countingFactory.CreateScopeCount.Should().Be(1);

        // 6 baseline messages + the attachment-bearing message + the trailing handoff system message.
        messages.Should().HaveCount(8);
        var attachmentMessage = messages[6];
        attachmentMessage.Content.Select(c => c.Text).Should().ContainInOrder(
            "switch text with files", "FILE_A", "FILE_B");

        mockAttachments.Verify(
            s => s.CreateOpenAiContentFromLoadedFileAsync(It.IsAny<NotebookFile>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        mockAttachments.Verify(
            s => s.CreateOpenAiContentFromNotebookFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
