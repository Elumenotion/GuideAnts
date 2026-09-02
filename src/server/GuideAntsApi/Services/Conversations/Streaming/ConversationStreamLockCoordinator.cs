namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class ConversationStreamLockCoordinator
{
    private readonly IDistributedConversationLock _distributedLock;

    public ConversationStreamLockCoordinator(IDistributedConversationLock distributedLock)
    {
        _distributedLock = distributedLock ?? throw new ArgumentNullException(nameof(distributedLock));
    }

    public async Task<IStreamLockHandle> AcquireAsync(
        Guid conversationId,
        string userName,
        SemaphoreSlim? semaphoreToRelease,
        ILogger logger,
        bool conversationLockEventSent,
        CancellationToken ct)
    {
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, userName, ct);
        switch (lockResult.Status)
        {
            case LockAcquisitionStatus.ConversationNotFound:
                throw new KeyNotFoundException($"Conversation {conversationId} not found");
            case LockAcquisitionStatus.AlreadyLocked:
                throw new InvalidOperationException($"Conversation is locked by {lockResult.LockedByUserName}");
            case LockAcquisitionStatus.RaceCondition:
                throw new InvalidOperationException("Conversation is locked by another user");
        }

        var acquiredLock = lockResult.Lock
            ?? throw new InvalidOperationException("Distributed lock acquisition returned no lease.");

        return new DistributedStreamLockHandle(
            conversationId,
            userName,
            acquiredLock.LeaseId,
            semaphoreToRelease,
            _distributedLock,
            logger,
            conversationLockEventSent);
    }
}
