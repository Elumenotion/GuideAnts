using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class PrivateConversationStreamPolicyTests
{
    [TestMethod]
    public async Task OrphanGateRepair_does_not_release_gate_while_distributed_acquisition_is_pending()
    {
        var conversationId = Guid.NewGuid();
        var acquisitionStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completeAcquisition = new TaskCompletionSource<LockAcquisitionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var distributedLock = new Mock<IDistributedConversationLock>();
        distributedLock
            .Setup(lockService => lockService.TryAcquireLockAsync(
                conversationId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, CancellationToken _) =>
            {
                acquisitionStarted.TrySetResult(true);
                return completeAcquisition.Task;
            });
        distributedLock
            .Setup(lockService => lockService.ReleaseLockAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        distributedLock
            .Setup(lockService => lockService.RenewLockAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var broadcastHub = new Mock<IConversationBroadcastHub>();
        broadcastHub
            .Setup(hub => hub.BroadcastToConversationAsync(
                It.IsAny<Guid>(),
                It.IsAny<StreamingEvent>()))
            .Returns(Task.CompletedTask);

        using var provider = new ServiceCollection().BuildServiceProvider();
        var policy = new PrivateConversationStreamPolicy(
            broadcastHub.Object,
            new ConversationStreamLockCoordinator(distributedLock.Object),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrivateConversationStreamPolicy>.Instance);

        var acquireTask = policy.TryAcquireStreamAsync(
            conversationId,
            new StreamUserIdentity(null, "stop-race-test", null),
            CancellationToken.None);

        await acquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        policy.TryReleaseOrphanedConversationGate(conversationId).Should().BeFalse();

        completeAcquisition.SetResult(LockAcquisitionResult.Acquired(new ConversationLock
        {
            ConversationId = conversationId,
            LockedByUserName = "stop-race-test",
            LockedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        }));

        var handle = await acquireTask;
        policy.GetConversationGate(conversationId)!.CurrentCount.Should().Be(1);
        await handle.ReleaseAsync(CancellationToken.None);
    }
}
