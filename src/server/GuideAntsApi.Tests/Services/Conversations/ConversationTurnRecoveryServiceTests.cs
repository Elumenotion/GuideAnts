using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Recovery;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationTurnRecoveryServiceTests
{
    [TestMethod]
    public async Task RecoverStaleTurns_skips_turn_still_in_flight_after_detach()
    {
        var (options, turnId, _) = await SeedStaleStreamingTurnAsync();
        var registry = new ConversationStreamRunRegistry();
        _ = registry.Register(turnId);
        registry.Detach(turnId).Should().BeTrue();

        var service = CreateService(options, registry);
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        await using var db = new ApplicationDbContext(options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("streaming");
        turn.TerminationCode.Should().BeNull();

        registry.Unregister(turnId);
    }

    [TestMethod]
    public async Task RequestPendingCancellationsAsync_ignores_terminal_cancel_requested_turns()
    {
        var (options, turnId, _) = await SeedStaleStreamingTurnAsync();
        await using (var db = new ApplicationDbContext(options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.Status = "cancelled";
            turn.TerminationCode = "cancel_requested";
            await db.SaveChangesAsync();
        }

        var registry = new ConversationStreamRunRegistry();
        using var workerCts = registry.Register(turnId);
        var service = CreateService(options, registry);

        await service.RequestPendingCancellationsAsync(CancellationToken.None);

        workerCts.Token.IsCancellationRequested.Should().BeFalse();
        registry.Unregister(turnId);
    }

    [TestMethod]
    public async Task RecoverStaleTurns_skips_turn_still_active_in_run_registry()
    {
        var (options, turnId, _) = await SeedStaleStreamingTurnAsync();
        var registry = new ConversationStreamRunRegistry();
        _ = registry.Register(turnId);

        var service = CreateService(options, registry);
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        await using var db = new ApplicationDbContext(options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("streaming");
        turn.TerminationCode.Should().BeNull();
    }

    [TestMethod]
    public async Task RequestPendingCancellationsAsync_signals_registered_turn()
    {
        var (options, turnId, _) = await SeedStaleStreamingTurnAsync();
        await using (var db = new ApplicationDbContext(options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.TerminationCode = "cancel_requested";
            await db.SaveChangesAsync();
        }

        var registry = new ConversationStreamRunRegistry();
        using var workerCts = registry.Register(turnId);
        var service = CreateService(options, registry);

        await service.RequestPendingCancellationsAsync(CancellationToken.None);

        workerCts.Token.IsCancellationRequested.Should().BeTrue();
        registry.Unregister(turnId);
    }

    [TestMethod]
    public async Task RecoverStaleTurns_skips_turn_with_active_distributed_lock()
    {
        var (options, turnId, conversationId) = await SeedStaleStreamingTurnAsync();
        await using (var db = new ApplicationDbContext(options))
        {
            db.ConversationLocks.Add(new ConversationLock
            {
                ConversationId = conversationId,
                LockedByUserName = "remote-worker",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options, new ConversationStreamRunRegistry());
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        await using var verify = new ApplicationDbContext(options);
        var turn = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("streaming");
        turn.TerminationCode.Should().BeNull();
    }

    [TestMethod]
    public async Task RecoverStaleTurns_claims_pending_cancellation_without_waiting_for_stale_cutoff()
    {
        var (options, turnId, _) = await SeedStaleStreamingTurnAsync();
        var originalExecutionId = Guid.Empty;
        await using (var db = new ApplicationDbContext(options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            originalExecutionId = turn.ExecutionId!.Value;
            turn.LastUpdated = DateTime.UtcNow;
            turn.TerminationCode = "cancel_requested";
            await db.SaveChangesAsync();
        }

        var service = CreateService(options, new ConversationStreamRunRegistry());
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        await using var verify = new ApplicationDbContext(options);
        var recovered = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        recovered.Status.Should().Be("cancelled");
        recovered.TerminationCode.Should().Be("cancelled");
        recovered.ExecutionId.Should().NotBe(originalExecutionId);
    }

    private static ConversationTurnRecoveryService CreateService(
        DbContextOptions<ApplicationDbContext> options,
        ConversationStreamRunRegistry registry,
        IConversationUsageReporter? usageReporter = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        services.AddScoped<IConversationUsageReporter>(_ => usageReporter ?? Mock.Of<IConversationUsageReporter>());
        services.AddScoped<IConversationPersistence>(_ =>
        {
            var persistence = new Mock<IConversationPersistence>();
            persistence
                .Setup(p => p.PruneIncompleteToolCallsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            return persistence.Object;
        });

        return new ConversationTurnRecoveryService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            registry,
            Mock.Of<ILogger<ConversationTurnRecoveryService>>());
    }

    [TestMethod]
    public async Task RecoverStaleTurns_records_tool_usage_for_claimed_turn()
    {
        var (options, turnId, conversationId) = await SeedStaleStreamingTurnAsync();
        await using (var db = new ApplicationDbContext(options))
        {
            var claimedTurn = await db.ConversationTurns.SingleAsync(t2 => t2.Id == turnId);
            claimedTurn.TerminationCode = "cancel_requested";
            claimedTurn.LastUpdated = DateTime.UtcNow;
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = ChatRole.Tool,
                FunctionName = "run_bash",
                ToolCallId = "call_1",
                Content = "ok",
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var usageReporter = new Mock<IConversationUsageReporter>();
        var service = CreateService(options, new ConversationStreamRunRegistry(), usageReporter.Object);
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        usageReporter.Verify(r => r.RecordToolCallUsageForTurnAsync(
            It.Is<ToolTurnUsageRequest>(q => q.ConversationId == conversationId && q.TurnIndex == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RecoverStaleTurns_records_chat_usage_from_turn_usage_json()
    {
        var (options, turnId, conversationId) = await SeedStaleStreamingTurnAsync();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Models.Add(new Model
            {
                ModelId = "m-recovery-test",
                DisplayName = "Recovery test model",
                Provider = "openai-compatible",
                Created = DateTime.UtcNow
            });
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.ModelDeploymentId = "m-recovery-test";
            turn.TerminationCode = "cancel_requested";
            turn.LastUpdated = DateTime.UtcNow;
            turn.UsageJson = "{\"prompt_tokens\":120,\"completion_tokens\":34,\"total_tokens\":154,\"CachedPromptTokens\":12}";
            await db.SaveChangesAsync();
        }

        var usageReporter = new Mock<IConversationUsageReporter>();
        var service = CreateService(options, new ConversationStreamRunRegistry(), usageReporter.Object);
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        usageReporter.Verify(r => r.RecordChatCompletionUsageAsync(
            It.Is<ChatCompletionUsageRequest>(q =>
                q.ConversationId == conversationId
                && q.TurnIndex == 1
                && q.ModelDeploymentId == "m-recovery-test"
                && q.Metrics.ValueInput == 120
                && q.Metrics.ValueOutput == 34
                && q.Metrics.ValueCachedInput == 12),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RecoverStaleTurns_claims_pending_cancellation_and_records_usage()
    {
        var (options, turnId, conversationId) = await SeedStaleStreamingTurnAsync();
        await using (var db = new ApplicationDbContext(options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.TerminationCode = "cancel_requested";
            turn.LastUpdated = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var usageReporter = new Mock<IConversationUsageReporter>();
        var service = CreateService(options, new ConversationStreamRunRegistry(), usageReporter.Object);
        await service.RecoverStaleTurnsAsync(CancellationToken.None);

        await using var verify = new ApplicationDbContext(options);
        (await verify.ConversationTurns.SingleAsync(t => t.Id == turnId)).Status.Should().Be("cancelled");
        usageReporter.Verify(r => r.RecordToolCallUsageForTurnAsync(
            It.Is<ToolTurnUsageRequest>(q => q.ConversationId == conversationId && q.TurnIndex == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<(DbContextOptions<ApplicationDbContext> Options, Guid TurnId, Guid ConversationId)> SeedStaleStreamingTurnAsync()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"turn-recovery-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var staleAt = DateTime.UtcNow - ConversationTurnRecoveryService.StaleAfter - TimeSpan.FromMinutes(1);

        await using (var seed = new ApplicationDbContext(options))
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
                Created = staleAt,
                LastUpdated = staleAt
            });
            await seed.SaveChangesAsync();
        }

        return (options, turnId, conversationId);
    }
}
