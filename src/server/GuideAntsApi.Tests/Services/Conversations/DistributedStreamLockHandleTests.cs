using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class DistributedStreamLockHandleTests
{
    [TestMethod]
    public async Task Without_BeginStreamingRenewal_lock_is_never_renewed()
    {
        var conversationId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.ReleaseLockAsync(conversationId, leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handle = new DistributedStreamLockHandle(
            conversationId,
            "Doug Ware",
            leaseId,
            semaphoreToRelease: null,
            distributedLock.Object,
            NullLogger.Instance,
            conversationLockEventSent: false);

        // Former bug: renewal started in the constructor, so a hung setup phase
        // extended ExpiresAt forever and Stop could not free the conversation.
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        distributedLock.Verify(
            l => l.RenewLockAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeTrue();
    }

    [TestMethod]
    public async Task BeginStreamingRenewal_is_idempotent_and_Release_stops_renewal_loop()
    {
        var conversationId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.RenewLockAsync(
                conversationId,
                leaseId,
                "Doug Ware",
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        distributedLock
            .Setup(l => l.ReleaseLockAsync(conversationId, leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handle = new DistributedStreamLockHandle(
            conversationId,
            "Doug Ware",
            leaseId,
            semaphoreToRelease: null,
            distributedLock.Object,
            NullLogger.Instance,
            conversationLockEventSent: false);

        handle.BeginStreamingRenewal();
        handle.BeginStreamingRenewal();

        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeTrue();
        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Release_failure_remains_retryable_until_distributed_lock_is_released()
    {
        var conversationId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .SetupSequence(l => l.ReleaseLockAsync(conversationId, leaseId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("temporary release failure"))
            .ReturnsAsync(true);

        var handle = new DistributedStreamLockHandle(
            conversationId,
            "Doug Ware",
            leaseId,
            semaphoreToRelease: null,
            distributedLock.Object,
            NullLogger.Instance,
            conversationLockEventSent: false);

        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeFalse();
        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeTrue();
        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Release_of_fenced_lease_does_not_release_or_announce_new_owner()
    {
        var conversationId = Guid.NewGuid();
        var oldLeaseId = Guid.NewGuid();
        var newLeaseId = Guid.NewGuid();
        var distributedLock = new Mock<IDistributedConversationLock>(MockBehavior.Strict);
        distributedLock
            .Setup(l => l.ReleaseLockAsync(conversationId, oldLeaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        distributedLock
            .Setup(l => l.GetActiveLockAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = newLeaseId,
                LockedByUserName = "new-owner",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var handle = new DistributedStreamLockHandle(
            conversationId,
            "old-owner",
            oldLeaseId,
            semaphoreToRelease: null,
            distributedLock.Object,
            NullLogger.Instance,
            conversationLockEventSent: true);

        (await handle.ReleaseAsync(CancellationToken.None)).Should().BeTrue();
        handle.ConversationLockEventSent.Should().BeFalse();
    }

    [TestMethod]
    public void LeaseLostToken_is_never_signalled_by_renewal_failures()
    {
        var source = File.ReadAllText(
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "GuideAntsApi",
                    "Services",
                    "Conversations",
                    "Streaming",
                    "DistributedStreamLockHandle.cs")));

        source.Should().NotContain("treating the lease as lost");
        source.Should().NotContain("_leaseLostCts.Cancel()");
    }

}
