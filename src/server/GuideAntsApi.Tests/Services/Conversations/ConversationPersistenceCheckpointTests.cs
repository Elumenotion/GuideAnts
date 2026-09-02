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
public sealed class ConversationPersistenceCheckpointTests
{
    [TestMethod]
    public async Task CheckpointTurnAsync_allows_pending_client_tool_status()
    {
        var (persistence, _, turnId, messageId, _) = await SeedStreamingTurnAsync();

        await using (var db = new ApplicationDbContext(_options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.Status = "pending_client_tool";
            await db.SaveChangesAsync();
        }

        (await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            "pending tool",
            null,
            checkpointVersion: 1,
            CancellationToken.None)).Should().BeTrue();
    }

    [TestMethod]
    public async Task CheckpointTurnAsync_advances_version_last_updated_and_thinking()
    {
        var (persistence, conversationId, turnId, messageId, staleLastUpdated) = await SeedStreamingTurnAsync();

        var ok = await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            content: string.Empty,
            thinkingBlocksJson: """[{"type":"thinking","thinking":"reasoning heartbeat","signature":""}]""",
            checkpointVersion: 1,
            CancellationToken.None);

        ok.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.CheckpointVersion.Should().Be(1);
        turn.LastUpdated.Should().BeAfter(staleLastUpdated);
        turn.Status.Should().Be("streaming");

        var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == messageId);
        assistant.ThinkingBlocksJson.Should().Contain("reasoning heartbeat");
    }

    [TestMethod]
    public async Task CheckpointTurnAsync_rejects_stale_checkpoint_version()
    {
        var (persistence, _, turnId, messageId, _) = await SeedStreamingTurnAsync();

        (await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            "first",
            null,
            checkpointVersion: 2,
            CancellationToken.None)).Should().BeTrue();

        (await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            "stale",
            null,
            checkpointVersion: 2,
            CancellationToken.None)).Should().BeFalse();

        await using var db = new ApplicationDbContext(_options);
        var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == messageId);
        assistant.Content.Should().Be("first");
    }

    [TestMethod]
    public async Task CheckpointTurnAsync_refuses_terminal_turn()
    {
        var (persistence, _, turnId, messageId, _) = await SeedStreamingTurnAsync();

        await using (var db = new ApplicationDbContext(_options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.Status = "interrupted";
            await db.SaveChangesAsync();
        }

        (await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            "late",
            null,
            checkpointVersion: 1,
            CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckpointTurnAsync_rejects_stale_execution_id()
    {
        var (persistence, _, turnId, messageId, _) = await SeedStreamingTurnAsync();

        Guid executionId;
        await using (var db = new ApplicationDbContext(_options))
        {
            executionId = (await db.ConversationTurns.SingleAsync(t => t.Id == turnId)).ExecutionId!.Value;
        }

        (await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            "stale worker",
            null,
            checkpointVersion: 1,
            CancellationToken.None,
            expectedExecutionId: Guid.NewGuid())).Should().BeFalse();

        await using (var db = new ApplicationDbContext(_options))
        {
            var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == messageId);
            assistant.Content.Should().BeEmpty();
        }

        (await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            "current worker",
            null,
            checkpointVersion: 1,
            CancellationToken.None,
            expectedExecutionId: executionId)).Should().BeTrue();
    }

    private DbContextOptions<ApplicationDbContext> _options = null!;

    private async Task<(
        ConversationPersistence Persistence,
        Guid ConversationId,
        Guid TurnId,
        Guid MessageId,
        DateTime StaleLastUpdated)> SeedStreamingTurnAsync()
    {
        _options = BackgroundJobTestHelpers.CreateInMemoryOptions($"checkpoint-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var staleLastUpdated = DateTime.UtcNow.AddMinutes(-10);

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
                ExecutionId = Guid.NewGuid(),
                Created = staleLastUpdated,
                LastUpdated = staleLastUpdated
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = messageId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = string.Empty,
                IsStreaming = true,
                Created = staleLastUpdated
            });
            await seed.SaveChangesAsync();
        }

        var db = new ApplicationDbContext(_options);
        var persistence = new ConversationPersistence(
            new TestServiceScopeFactory(db),
            Mock.Of<ILogger<ConversationPersistence>>());
        return (persistence, conversationId, turnId, messageId, staleLastUpdated);
    }
}
