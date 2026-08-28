using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationPersistenceTurnStatusTests
{
    private ApplicationDbContext _dbContext = null!;
    private ConversationPersistence _persistence = null!;
    private Guid _conversationId;

    [TestInitialize]
    public void TestInitialize()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _conversationId = Guid.NewGuid();
        var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "NB" };
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = notebook.Id,
            Title = "Convo",
            Notebook = notebook
        });
        _dbContext.SaveChanges();

        (_persistence, _) = ConversationTestServices.CreatePersistence(new TestServiceScopeFactory(_dbContext));
    }

    [TestCleanup]
    public void TestCleanup() => _dbContext.Dispose();

    [TestMethod]
    public async Task CreateNextTurn_WithStreamingInitialStatus_IsBornStreamingWithExecutionId()
    {
        var result = await _persistence.CreateNextTurnAsync(
            new CreateTurnRequest(_conversationId, "Claude", "gpt-4o-mini", "Hi", InitialStatus: "streaming"));

        result.Turn.Status.Should().Be("streaming");
        result.Turn.ExecutionId.Should().NotBeNull();
        result.TurnIndex.Should().Be(1);
    }
}
