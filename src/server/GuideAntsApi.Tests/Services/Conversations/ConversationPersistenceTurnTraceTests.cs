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
public sealed class ConversationPersistenceTurnTraceTests
{
    [TestMethod]
    public async Task AppendTurnTraceSegmentAsync_skips_when_turn_row_is_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"trace-missing-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        var missingTurnId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            var projectId = Guid.NewGuid();
            var notebookId = Guid.NewGuid();
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
            await seed.SaveChangesAsync();
        }

        var persistence = new ConversationPersistence(
            new TestServiceScopeFactory(new ApplicationDbContext(options)),
            Mock.Of<ILogger<ConversationPersistence>>());

        await persistence.AppendTurnTraceSegmentAsync(
            new AppendTurnTraceSegmentRequest(
                missingTurnId,
                conversationId,
                TurnIndex: 6,
                SchemaVersion: 1,
                CaptureState: "cancelled",
                SegmentJson: """{"captureState":"cancelled"}"""));

        await using var db = new ApplicationDbContext(options);
        (await db.ConversationTurnTraces.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task AppendTurnTraceSegmentAsync_inserts_when_turn_exists()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"trace-ok-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            var projectId = Guid.NewGuid();
            var notebookId = Guid.NewGuid();
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
                Status = "cancelled",
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var persistence = new ConversationPersistence(
            new TestServiceScopeFactory(new ApplicationDbContext(options)),
            Mock.Of<ILogger<ConversationPersistence>>());

        await persistence.AppendTurnTraceSegmentAsync(
            new AppendTurnTraceSegmentRequest(
                turnId,
                conversationId,
                TurnIndex: 1,
                SchemaVersion: 1,
                CaptureState: "cancelled",
                SegmentJson: """{"captureState":"cancelled"}"""));

        await using var db = new ApplicationDbContext(options);
        var trace = await db.ConversationTurnTraces.SingleAsync();
        trace.ConversationTurnId.Should().Be(turnId);
        trace.CaptureState.Should().Be("cancelled");
    }
}
