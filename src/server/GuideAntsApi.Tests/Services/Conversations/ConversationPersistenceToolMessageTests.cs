using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationPersistenceToolMessageTests
{
    [TestMethod]
    public async Task CreateToolMessageAsync_InsertsWhenToolCallIdIsNew()
    {
        var (persistence, conversationId, turnId) = await SeedAsync();

        var result = await persistence.CreateToolMessageAsync(new CreateToolMessageRequest(
            conversationId,
            turnId,
            TurnIndex: 1,
            MessageSequence: 5,
            Content: "{\"status\":\"ok\"}",
            ToolCallId: "call_abc",
            FunctionName: "QueryData",
            AssistantId: null,
            AssistantName: null));

        result.Created.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var rows = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.ToolCallId == "call_abc")
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Content.Should().Be("{\"status\":\"ok\"}");
        rows[0].MessageSequence.Should().Be(5);
    }

    [TestMethod]
    public async Task CreateToolMessageAsync_UpdatesInPlaceAndRemovesDuplicates_WhenToolCallIdExists()
    {
        var (persistence, conversationId, turnId) = await SeedAsync();
        var firstId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage
                {
                    Id = firstId,
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 5,
                    Role = ChatRole.Tool,
                    Content = new string('x', 10_000),
                    ToolCallId = "call_dup",
                    FunctionName = "QueryData",
                    Created = DateTime.UtcNow.AddSeconds(-2)
                },
                new NotebookConversationMessage
                {
                    Id = duplicateId,
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 6,
                    Role = ChatRole.Tool,
                    Content = "[stale abort]",
                    ToolCallId = "call_dup",
                    FunctionName = "QueryData",
                    Created = DateTime.UtcNow.AddSeconds(-1)
                });
            await seed.SaveChangesAsync();
        }

        var notice = "[Message aborted due to size restrictions]";
        var result = await persistence.CreateToolMessageAsync(new CreateToolMessageRequest(
            conversationId,
            turnId,
            TurnIndex: 1,
            MessageSequence: 99,
            Content: notice,
            ToolCallId: "call_dup",
            FunctionName: "QueryData",
            AssistantId: null,
            AssistantName: null));

        result.Created.Should().BeFalse();
        result.MessageId.Should().Be(firstId);

        await using var db = new ApplicationDbContext(_options);
        var rows = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.ToolCallId == "call_dup")
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(firstId);
        rows[0].Content.Should().Be(notice);
        rows[0].MessageSequence.Should().Be(5);
    }

    private DbContextOptions<ApplicationDbContext> _options = null!;

    private async Task<(ConversationPersistence Persistence, Guid ConversationId, Guid TurnId)> SeedAsync()
    {
        _options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-msg-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.Projects.Add(new Project { Id = projectId, Title = "P", Slug = "p", Created = DateTime.UtcNow });
            seed.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "NB",
                Slug = "nb",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Chat",
                Created = DateTime.UtcNow
            });
            seed.ConversationTurns.Add(new ConversationTurn
            {
                Id = turnId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                AssistantName = "Guide",
                Status = "streaming",
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var db = new ApplicationDbContext(_options);
        var persistence = new ConversationPersistence(
            new TestServiceScopeFactory(db),
            Mock.Of<ILogger<ConversationPersistence>>());
        return (persistence, conversationId, turnId);
    }
}
