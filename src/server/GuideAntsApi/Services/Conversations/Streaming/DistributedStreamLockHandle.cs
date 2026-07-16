namespace GuideAntsApi.Services.Conversations.Streaming;

internal sealed class DistributedStreamLockHandle : IStreamLockHandle
{
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExtendBy = TimeSpan.FromMinutes(5);

    private readonly Guid _conversationId;
    private readonly string _userName;
    private readonly SemaphoreSlim? _semaphoreToRelease;
    private readonly IDistributedConversationLock _distributedLock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _renewalCts = new();
    private readonly Task _renewalTask;
    private int _released;

    public DistributedStreamLockHandle(
        Guid conversationId,
        string userName,
        SemaphoreSlim? semaphoreToRelease,
        IDistributedConversationLock distributedLock,
        ILogger logger,
        bool conversationLockEventSent)
    {
        _conversationId = conversationId;
        _userName = userName;
        _semaphoreToRelease = semaphoreToRelease;
        _distributedLock = distributedLock;
        _logger = logger;
        ConversationLockEventSent = conversationLockEventSent;
        _renewalTask = Task.Run(RunRenewalLoopAsync);
    }

    public bool ConversationLockEventSent { get; }

    public async Task<bool> ReleaseAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return false;
        }

        _renewalCts.Cancel();
        try
        {
            await _renewalTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown/release.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected lock-renewal shutdown error for {ConversationId}", _conversationId);
        }
        finally
        {
            _renewalCts.Dispose();
        }

        if (_semaphoreToRelease != null)
        {
            try
            {
                _semaphoreToRelease.Release();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", _conversationId);
            }
        }

        try
        {
            await _distributedLock.ReleaseLockAsync(_conversationId, ct);
            _logger.LogInformation("Released conversation lock for {ConversationId}", _conversationId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release distributed conversation lock for {ConversationId}", _conversationId);
            return false;
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
