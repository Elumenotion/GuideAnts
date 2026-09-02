namespace GuideAntsApi.Services.Conversations.Streaming;

internal sealed class DistributedStreamLockHandle : IStreamLockHandle
{
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExtendBy = TimeSpan.FromMinutes(5);

    private readonly Guid _conversationId;
    private readonly string _userName;
    private readonly Guid _leaseId;
    private readonly SemaphoreSlim? _semaphoreToRelease;
    private readonly IDistributedConversationLock _distributedLock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _renewalCts = new();
    private readonly CancellationTokenSource _leaseLostCts = new();
    private readonly object _renewalGate = new();
    private readonly SemaphoreSlim _releaseGate = new(1, 1);
    private readonly bool _conversationLockEventWasSent;
    private Task? _renewalTask;
    private int _releaseStarted;
    private int _localReleaseCompleted;
    private int _releaseCompleted;
    private int _ownLeaseReleased;

    public DistributedStreamLockHandle(
        Guid conversationId,
        string userName,
        Guid leaseId,
        SemaphoreSlim? semaphoreToRelease,
        IDistributedConversationLock distributedLock,
        ILogger logger,
        bool conversationLockEventSent)
    {
        _conversationId = conversationId;
        _userName = userName;
        _leaseId = leaseId;
        _semaphoreToRelease = semaphoreToRelease;
        _distributedLock = distributedLock;
        _logger = logger;
        _conversationLockEventWasSent = conversationLockEventSent;
    }

    public Guid LeaseId => _leaseId;

    public bool ConversationLockEventSent =>
        _conversationLockEventWasSent && Volatile.Read(ref _ownLeaseReleased) == 1;

    public CancellationToken LeaseLostToken => _leaseLostCts.Token;

    public void BeginStreamingRenewal()
    {
        if (Volatile.Read(ref _releaseStarted) == 1)
        {
            return;
        }

        lock (_renewalGate)
        {
            if (_renewalTask != null || Volatile.Read(ref _releaseStarted) == 1)
            {
                return;
            }

            _renewalTask = Task.Run(RunRenewalLoopAsync);
        }
    }

    public async Task<bool> ReleaseAsync(CancellationToken ct)
    {
        await _releaseGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _releaseCompleted) == 1)
            {
                return false;
            }

            if (Interlocked.Exchange(ref _releaseStarted, 1) == 0)
            {
                _renewalCts.Cancel();
                Task? renewalTask;
                lock (_renewalGate)
                {
                    renewalTask = _renewalTask;
                }

                if (renewalTask != null)
                {
                    try
                    {
                        await renewalTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on shutdown/release.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unexpected lock-renewal shutdown error for {ConversationId}", _conversationId);
                    }
                }

                _renewalCts.Dispose();
                _leaseLostCts.Dispose();

            }

            if (_semaphoreToRelease != null && Volatile.Read(ref _localReleaseCompleted) == 0)
            {
                try
                {
                    _semaphoreToRelease.Release();
                    Interlocked.Exchange(ref _localReleaseCompleted, 1);
                }
                catch (SemaphoreFullException)
                {
                    // The gate is already available. Treat this as an idempotent release so a
                    // distributed-release retry cannot strand the local gate.
                    Interlocked.Exchange(ref _localReleaseCompleted, 1);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", _conversationId);
                    return false;
                }
            }

            try
            {
                var released = await _distributedLock.ReleaseLockAsync(_conversationId, _leaseId, ct);
                if (released)
                {
                    Interlocked.Exchange(ref _ownLeaseReleased, 1);
                }
                else
                {
                    // The lease may have expired and been replaced. The old worker is no longer
                    // allowed to publish an unlock for the newer owner, but it is safe to finish
                    // unregistering this stale worker once its lease is no longer active.
                    var activeLock = await _distributedLock.GetActiveLockAsync(_conversationId, ct);
                    if (activeLock?.LeaseId == _leaseId)
                    {
                        return false;
                    }
                }

                Interlocked.Exchange(ref _releaseCompleted, 1);
                _logger.LogInformation("Released conversation lock for {ConversationId}", _conversationId);
                return true;
            }
            catch (Exception ex)
            {
                // Keep the handle retryable. The stream lifecycle must not signal completion
                // while the distributed lock may still exist.
                _logger.LogError(
                    ex,
                    "Failed to release distributed lock for {ConversationId}; release remains retryable",
                    _conversationId);
                return false;
            }
        }
        finally
        {
            _releaseGate.Release();
        }
    }

    private async Task RunRenewalLoopAsync()
    {
        while (!_renewalCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RenewEvery, _renewalCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var renewed = await _distributedLock.RenewLockAsync(
                    _conversationId,
                    _leaseId,
                    _userName,
                    ExtendBy,
                    _renewalCts.Token);

                if (!renewed)
                {
                    _logger.LogWarning(
                        "Conversation lock renewal was not applied for {ConversationId}; lock may have been lost.",
                        _conversationId);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error renewing distributed conversation lock for {ConversationId}", _conversationId);
            }
        }
    }
}
