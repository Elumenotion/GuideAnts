using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationPersistenceTerminalizeTests
{
    [TestMethod]
    public async Task TerminalizeTurnAsync_IsIdempotentWhenTurnAlreadyTerminal()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("completed");
        var assistantId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "Final answer.",
                IsStreaming = false,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var output = new ChatRunOutput
        {
            Status = "completed",
            Usage = new UsageResponse { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 }
        };

        var first = await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "completed",
            Output: output));

        var second = await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "failed",
            TerminationCode: "retry",
            Output: output));

        first.Should().BeTrue();
        second.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("completed");
        turn.TerminationCode.Should().BeNull();
        turn.ChatRunOutputJson.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task TerminalizeTurnAsync_FinalizesStreamingAssistantRows()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var assistantId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "streaming partial",
                IsStreaming = true,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "cancelled",
            TerminationCode: "cancelled",
            AssistantSnapshots: [
                new TerminalizeAssistantSnapshot(assistantId, "streaming partial final")
            ],
            PruneIncompleteToolCalls: true));

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("cancelled");
        turn.TerminalizedAt.Should().NotBeNull();

        var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == assistantId);
        assistant.IsStreaming.Should().BeFalse();
        assistant.Content.Should().Be("streaming partial final");
    }

    private DbContextOptions<ApplicationDbContext> _options = null!;

    private async Task<(ConversationPersistence Persistence, Guid ConversationId, Guid TurnId)> SeedAsync(string turnStatus)
    {
        _options = BackgroundJobTestHelpers.CreateInMemoryOptions($"terminalize-{Guid.NewGuid():N}");
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
                Status = turnStatus,
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                TerminalizedAt = turnStatus == "completed" ? DateTime.UtcNow : null
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
