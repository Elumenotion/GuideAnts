using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationUndoServiceStreamGuardTests
{
    [TestMethod]
    public async Task UndoLastWithoutLock_refuses_when_target_turn_is_still_registered()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"undo-guard-{Guid.NewGuid():N}");
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
                Status = "streaming",
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "hi",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var registry = new ConversationStreamRunRegistry();
        using var cts = registry.Register(turnId);

        var distributedLock = Mock.Of<IDistributedConversationLock>();
        var broadcastHub = Mock.Of<IConversationBroadcastHub>();
        var scopeFactory = new TestServiceScopeFactory(new ApplicationDbContext(options));
        var policy = new PrivateConversationStreamPolicy(
            broadcastHub,
            new ConversationStreamLockCoordinator(distributedLock),
            scopeFactory,
            Mock.Of<ILogger<PrivateConversationStreamPolicy>>());

        var service = new ConversationUndoService(
            distributedLock,
            broadcastHub,
            policy,
            registry,
            scopeFactory,
            Mock.Of<ILogger<ConversationUndoService>>());

        var act = () => service.UndoLastWithoutLockAsync(conversationId);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*still streaming*");

        await using var db = new ApplicationDbContext(options);
        (await db.ConversationTurns.CountAsync()).Should().Be(1);
        (await db.NotebookConversationMessages.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task Undo_with_remote_distributed_lock_does_not_release_or_delete_remote_turn()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"undo-remote-{Guid.NewGuid():N}");
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
                Status = "completed",
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "hi",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(lockService => lockService.TryAcquireLockAsync(
                conversationId,
                "User",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockAcquisitionResult.AlreadyLocked("remote-worker"));
        var broadcastHub = Mock.Of<IConversationBroadcastHub>();
        var registry = new ConversationStreamRunRegistry();
        var scopeFactory = new TestServiceScopeFactory(new ApplicationDbContext(options));
        var policy = new PrivateConversationStreamPolicy(
            broadcastHub,
            new ConversationStreamLockCoordinator(distributedLock.Object),
            scopeFactory,
            Mock.Of<ILogger<PrivateConversationStreamPolicy>>());
        var service = new ConversationUndoService(
            distributedLock.Object,
            broadcastHub,
            policy,
            registry,
            scopeFactory,
            Mock.Of<ILogger<ConversationUndoService>>());

        var act = () => service.UndoLastForConversationAsync(conversationId);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*locked by remote-worker*");

        distributedLock.Verify(
            lockService => lockService.ReleaseLockAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        await using var db = new ApplicationDbContext(options);
        (await db.ConversationTurns.CountAsync()).Should().Be(1);
        (await db.NotebookConversationMessages.CountAsync()).Should().Be(1);
    }
}
