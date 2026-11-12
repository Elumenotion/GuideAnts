using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

[TestClass]
public sealed class ConversationTurnRecoverySqlIntegrationTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task TryClaimStaleTurnAsync_only_one_concurrent_claim_succeeds()
    {
        var (turnId, executionId, cutoff, _) = await SeedStaleStreamingTurnAsync();

        await using var db1 = SharedFactory!.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var db2 = SharedFactory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var claim1 = ConversationTurnRecoveryService.TryClaimStaleTurnAsync(db1, turnId, executionId, cutoff, CancellationToken.None);
        var claim2 = ConversationTurnRecoveryService.TryClaimStaleTurnAsync(db2, turnId, executionId, cutoff, CancellationToken.None);
        var results = await Task.WhenAll(claim1, claim2);

        results.Count(r => r).Should().Be(1);

        await using var verify = SharedFactory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var turn = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("interrupted");
        turn.TerminationCode.Should().Be("stream_interrupted");
    }

    [TestMethod]
    public async Task TryClaimStaleTurnAsync_loses_when_checkpoint_advances_last_updated()
    {
        var (turnId, executionId, cutoff, messageId) = await SeedStaleStreamingTurnAsync(includeAssistant: true);
        var persistence = SharedFactory!.Services.CreateScope().ServiceProvider.GetRequiredService<IConversationPersistence>();

        var checkpointed = await persistence.CheckpointTurnAsync(
            turnId,
            messageId,
            content: string.Empty,
            thinkingBlocksJson: """[{"type":"thinking","thinking":"heartbeat","signature":""}]""",
            checkpointVersion: 1,
            CancellationToken.None);
        checkpointed.Should().BeTrue();

        await using var db = SharedFactory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var claimed = await ConversationTurnRecoveryService.TryClaimStaleTurnAsync(
            db,
            turnId,
            executionId,
            cutoff,
            CancellationToken.None);

        claimed.Should().BeFalse();

        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("streaming");
        turn.CheckpointVersion.Should().Be(1);
        turn.LastUpdated.Should().BeOnOrAfter(cutoff);
    }

    [TestMethod]
    public async Task RecoverStaleTurns_terminalizes_orphaned_streaming_turn_on_sql()
    {
        var (turnId, _, _, _) = await SeedStaleStreamingTurnAsync(includeAssistant: true);
        using var scope = SharedFactory!.Services.CreateScope();
        var service = new ConversationTurnRecoveryService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Conversations.Streaming.ConversationStreamRunRegistry>(),
            scope.ServiceProvider.GetRequiredService<ILogger<ConversationTurnRecoveryService>>());

        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        // The seeded turn is already stale, so the host's own ConversationTurnRecoveryService - which
        // sweeps every second against the same database - can claim it before this call does. Claiming
        // (an ExecuteUpdate that sets Status="interrupted") and the preview repair that follows it are
        // two separate writes, so reading immediately after RecoverStaleTurnsAsync returns can observe
        // the winner's claim before the winner's repair. Wait for the terminal state instead of for
        // this particular sweep call: the contract under test is that recovery leaves the orphan
        // terminalized, whichever sweep in the process performs it.
        await WaitForRecoveryAsync(turnId);

        await using var verify = SharedFactory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var turn = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("interrupted");
        turn.TerminationCode.Should().Be("stream_interrupted");

        var assistant = await verify.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == turn.NotebookConversationId && m.Role == DataModelChatRole.Assistant)
            .SingleAsync();
        assistant.IsStreaming.Should().BeFalse();
    }

    private static async Task WaitForRecoveryAsync(Guid turnId, TimeSpan? timeout = null)
    {
        var waitTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitTimeout;

        Guid conversationId;
        await using (var lookup = SharedFactory!.Services.CreateScope().ServiceProvider
            .GetRequiredService<ApplicationDbContext>())
        {
            conversationId = await lookup.ConversationTurns
                .AsNoTracking()
                .Where(t => t.Id == turnId)
                .Select(t => t.NotebookConversationId)
                .SingleAsync();
        }

        while (DateTime.UtcNow < deadline)
        {
            await using var probe = SharedFactory.Services.CreateScope().ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            var status = await probe.ConversationTurns
                .AsNoTracking()
                .Where(t => t.Id == turnId)
                .Select(t => t.Status)
                .SingleAsync();

            var streamingPreviews = await probe.NotebookConversationMessages
                .AsNoTracking()
                .CountAsync(m => m.NotebookConversationId == conversationId
                    && m.Role == DataModelChatRole.Assistant
                    && m.IsStreaming == true);

            if (!string.Equals(status, "streaming", StringComparison.OrdinalIgnoreCase) && streamingPreviews == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            $"recovery did not finish terminalizing turn {turnId} and clearing its streaming preview " +
            $"within {waitTimeout.TotalSeconds}s");
    }

    private async Task<(Guid TurnId, Guid ExecutionId, DateTime Cutoff, Guid MessageId)> SeedStaleStreamingTurnAsync(
        bool includeAssistant = false)
    {
        var cutoff = DateTime.UtcNow - ConversationTurnRecoveryService.StaleAfter - TimeSpan.FromMinutes(1);
        var staleAt = cutoff.AddMinutes(-1);
        var turnId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive)
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Recovery SQL",
            Slug = $"recovery-{Guid.NewGuid():N}",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = "Recovery NB",
            Slug = $"nb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);

        db.NotebookConversations.Add(new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebook.Id,
            Title = "Recovery convo",
            Created = DateTime.UtcNow
        });

        db.ConversationTurns.Add(new ConversationTurn
        {
            Id = turnId,
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            AssistantName = "Guide",
            Status = "streaming",
            ExecutionId = executionId,
            Created = staleAt,
            LastUpdated = staleAt
        });

        if (includeAssistant)
        {
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = messageId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "partial",
                IsStreaming = true,
                Created = staleAt
            });
        }

        await db.SaveChangesAsync();
        return (turnId, executionId, cutoff, messageId);
    }
}
