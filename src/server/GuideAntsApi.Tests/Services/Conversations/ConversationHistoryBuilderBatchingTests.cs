using AntRunner.Chat.Abstractions;
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
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationHistoryBuilderBatchingTests
{
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
    public void TestCleanup() => _dbContext.Dispose();

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
}
