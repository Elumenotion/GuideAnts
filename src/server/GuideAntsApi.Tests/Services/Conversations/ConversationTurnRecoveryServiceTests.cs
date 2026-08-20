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

    private static ConversationTurnRecoveryService CreateService(
        DbContextOptions<ApplicationDbContext> options,
        ConversationStreamRunRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
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
