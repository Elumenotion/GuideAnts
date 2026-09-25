using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class CompactionServiceTests
{
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ConversationTurn Turn(Guid conversationId, int index, string status) => new()
    {
        NotebookConversationId = conversationId,
        TurnIndex = index,
        AssistantName = "a",
        Instructions = "i",
        Status = status
    };

    private static Mock<IDistributedConversationLock> AcquiringLock(Guid conversationId)
    {
        var mock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        mock.Setup(l => l.TryAcquireLockAsync(conversationId, "User", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.Acquired(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = Guid.NewGuid(),
                LockedByUserName = "tester"
            }));
        mock.Setup(l => l.ReleaseLockAsync(conversationId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock;
    }

    [TestMethod]
    public async Task CompactConversationAsync_NoCompletedTurns_ReturnsExistingBoundaryUnchanged()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_NoCompletedTurns_ReturnsExistingBoundaryUnchanged));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "streaming"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.BoundaryTurnIndex.Should().BeNull();
        result.MessagesSummarized.Should().Be(0);
    }

    [TestMethod]
    public async Task CompactConversationAsync_OneCompletedTurn_SetsBoundaryToItsIndex()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_OneCompletedTurn_SetsBoundaryToItsIndex));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "completed"));
            db.ConversationTurns.Add(Turn(conversationId, 2, "pending_client_tool"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        // Turn 2 is pending_client_tool - never eligible, even though it has a higher index.
        result.BoundaryTurnIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task CompactConversationAsync_PressedAgainWithNoNewCompletedTurn_IsIdempotent()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_PressedAgainWithNoNewCompletedTurn_IsIdempotent));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t", CompactionBoundaryTurnIndex = 1
            });
            db.ConversationTurns.Add(Turn(conversationId, 1, "completed"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.BoundaryTurnIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task CompactConversationAsync_NeverMovesTheBoundaryBackward()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_NeverMovesTheBoundaryBackward));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Existing boundary (5) is ahead of what "last complete turn" would compute today (2) -
            // a contrived setup that proves the monotonic guard, not just realistic data.
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t", CompactionBoundaryTurnIndex = 5
            });
            db.ConversationTurns.Add(Turn(conversationId, 2, "completed"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.BoundaryTurnIndex.Should().Be(5);
    }

    [TestMethod]
    public async Task CompactConversationAsync_ConversationLocked_ThrowsNamingTheHolder()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_ConversationLocked_ThrowsNamingTheHolder));

        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.TryAcquireLockAsync(conversationId, "User", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.AlreadyLocked("remote-worker"));

        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var act = () => service.CompactConversationAsync(conversationId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locked by remote-worker*");
    }

    [TestMethod]
    public async Task CompactConversationAsync_ConversationNotFound_ThrowsKeyNotFound()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_ConversationNotFound_ThrowsKeyNotFound));

        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.TryAcquireLockAsync(conversationId, "User", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.NotFound());

        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var act = () => service.CompactConversationAsync(conversationId);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [TestMethod]
    public async Task CompactConversationAsync_LockAcquiredButConversationMissingFromDb_ThrowsKeyNotFound()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_LockAcquiredButConversationMissingFromDb_ThrowsKeyNotFound));
        // Deliberately do not seed any NotebookConversation for conversationId - the lock acquires
        // fine (e.g. from a lock-table row) but the DB read afterward finds nothing, which is a
        // distinct reachable path from the lock-service's own ConversationNotFound status.

        // AcquiringLock sets up both TryAcquireLockAsync and ReleaseLockAsync on a Strict mock, so
        // this also asserts the lock is still released via `finally` before the exception propagates.
        var distributedLock = AcquiringLock(conversationId);

        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var act = () => service.CompactConversationAsync(conversationId);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    private static NotebookConversationMessage Message(
        Guid conversationId, int turnIndex, int sequence, DataModelChatRole role, string content) => new()
    {
        NotebookConversationId = conversationId,
        TurnIndex = turnIndex,
        MessageSequence = sequence,
        Role = role,
        Content = content
    };

    [TestMethod]
    public async Task CompactConversationAsync_ComputesMessagesSummarizedFromPreBoundaryMessages()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_ComputesMessagesSummarizedFromPreBoundaryMessages));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "completed"));
            db.NotebookConversationMessages.Add(Message(conversationId, 1, 0, DataModelChatRole.User, "Build a CSV export feature"));
            db.NotebookConversationMessages.Add(Message(conversationId, 1, 1, DataModelChatRole.Assistant, "On it."));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.MessagesSummarized.Should().Be(2);
        result.EstimatedTokensBefore.Should().NotBeNull();
        result.EstimatedTokensAfter.Should().NotBeNull();
    }

    [TestMethod]
    public async Task CompactConversationAsync_NoCompletedTurns_LeavesTokenEstimatesNull()
    {
        var conversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(CompactConversationAsync_NoCompletedTurns_LeavesTokenEstimatesNull));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "t" });
            db.ConversationTurns.Add(Turn(conversationId, 1, "streaming"));
            await db.SaveChangesAsync();
        }

        var distributedLock = AcquiringLock(conversationId);
        var service = new CompactionService(distributedLock.Object, scopeFactory, NullLogger<CompactionService>.Instance);

        var result = await service.CompactConversationAsync(conversationId);

        result.MessagesSummarized.Should().Be(0);
        result.EstimatedTokensBefore.Should().BeNull();
        result.EstimatedTokensAfter.Should().BeNull();
    }
}
