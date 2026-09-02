using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public abstract class ConversationStreamPolicyBase : IConversationStreamPolicy
{
    private static readonly TimeSpan LockCleanupAttemptTimeout = TimeSpan.FromSeconds(1);
    private readonly ConversationStreamLockCoordinator _lockCoordinator;
    private readonly ILogger _logger;

    protected ConversationStreamPolicyBase(
        ConversationStreamLockCoordinator lockCoordinator,
        ILogger logger)
    {
        _lockCoordinator = lockCoordinator ?? throw new ArgumentNullException(nameof(lockCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public abstract ConversationUsageMode UsageMode { get; }

    public abstract bool SupportsExternalToolResume { get; }

    public abstract bool UsesProgressThrottling { get; }

    public abstract Task<StreamUserIdentity> ResolveUserIdentityAsync(Guid? internalUserId, string? externalUserIdentity, CancellationToken ct);

    public ConversationFileUrlContext BuildFileUrlContext(
        NotebookConversation conversation,
        string? publisherId,
        string? hostUrl) =>
        new(
            conversation.Notebook.ProjectId,
            conversation.NotebookId,
            conversation.Id,
            publisherId,
            hostUrl);

    public abstract string SanitizeAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx);

    public abstract string SanitizeToolContent(string content, ConversationFileUrlContext ctx);

    public abstract void UpdateFilenameUrlMapFromToolMessage(
        string sanitizedToolContent,
        ConversationFileUrlContext ctx,
        IDictionary<string, string> filenameUrlMap,
        NotebookConversation conversation);

    public async Task<IStreamLockHandle> TryAcquireStreamAsync(
        Guid conversationId,
        StreamUserIdentity user,
        CancellationToken ct)
    {
        var localGate = await TryAcquireLocalGateAsync(conversationId, ct);

        IStreamLockHandle distributedHandle;
        try
        {
            distributedHandle = await _lockCoordinator.AcquireAsync(
                conversationId,
                user.UserName,
                semaphoreToRelease: null,
                _logger,
                ShouldEmitConversationLockEvent,
                ct);
        }
        catch
        {
            LocalGateAcquisitionResolved(conversationId);
            ReleaseLocalGate(localGate, conversationId);
            throw;
        }

        LocalGateAcquisitionResolved(conversationId);
        // The distributed lease, rather than this process-local semaphore, owns the lifetime of
        // the stream. Releasing the local admission gate immediately is required for a Stop
        // handled by another API instance: that instance can remove the distributed lease, and a
        // replacement request on this instance must not wait for the old provider worker to exit.
        ReleaseLocalGate(localGate, conversationId);
        localGate = null;

        try
        {
            await OnLockAcquiredAsync(conversationId, user, ct);
        }
        catch
        {
            await ReleaseDistributedLockUntilConfirmedAsync(distributedHandle, conversationId);
            throw;
        }

        return distributedHandle;
    }

    public virtual Task OnTurnCreatedAsync(Guid conversationId, StreamTurnCreatedInfo info, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task OnStreamingStartedAsync(Guid conversationId, StreamStreamingStartedInfo info, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task OnUnlockAsync(Guid conversationId, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task OnCompleteAsync(Guid conversationId, Guid turnId, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
        Guid turnId,
        int contentLength,
        int tokensProcessed,
        CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual bool ShouldEmitConversationLockEvent => false;

    protected virtual Task<SemaphoreSlim?> TryAcquireLocalGateAsync(Guid conversationId, CancellationToken ct) =>
        Task.FromResult<SemaphoreSlim?>(null);

    protected virtual Task OnLockAcquiredAsync(Guid conversationId, StreamUserIdentity user, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Signals that distributed lock acquisition has resolved for a locally gated stream.
    /// Policies with an orphan-gate repair path can use this to close the acquisition race
    /// without treating a request that is still waiting for the distributed lock as orphaned.
    /// </summary>
    protected virtual void LocalGateAcquisitionResolved(Guid conversationId)
    {
    }

    private void ReleaseLocalGate(SemaphoreSlim? localGate, Guid conversationId)
    {
        if (localGate is null)
        {
            return;
        }

        try
        {
            localGate.Release();
        }
        catch (SemaphoreFullException)
        {
            // The gate is already available; cleanup is idempotent.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", conversationId);
        }
    }

    private async Task ReleaseDistributedLockUntilConfirmedAsync(
        IStreamLockHandle lockHandle,
        Guid conversationId)
    {
        for (var releaseAttempt = 1; releaseAttempt <= 4; releaseAttempt++)
        {
            Task<bool>? releaseTask = null;
            try
            {
                releaseTask = lockHandle.ReleaseAsync(CancellationToken.None);
                if (await releaseTask.WaitAsync(LockCleanupAttemptTimeout).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (TimeoutException)
            {
                if (releaseTask != null)
                {
                    _ = ObserveReleaseTaskAsync(releaseTask);
                }

                _logger.LogWarning(
                    "Timed out releasing conversation lock for {ConversationId}; the lease will expire",
                    conversationId);
                return;
            }
            catch (Exception ex)
            {
                if (releaseAttempt == 1 || releaseAttempt == 4)
                {
                    _logger.LogWarning(
                        ex,
                        "Conversation lock cleanup attempt {Attempt} failed for {ConversationId}; retrying",
                        releaseAttempt,
                        conversationId);
                }
            }

            if (releaseAttempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            }
        }
    }

    private static async Task ObserveReleaseTaskAsync(Task<bool> releaseTask)
    {
        try
        {
            await releaseTask.ConfigureAwait(false);
        }
        catch
        {
            // The bounded cleanup attempt already returned; observe late release failures.
        }
    }

    private sealed class LocalGateStreamLockHandle : IStreamLockHandle
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IStreamLockHandle _inner;
        private readonly ILogger _logger;
        private readonly Guid _conversationId;
        private readonly SemaphoreSlim _releaseGate = new(1, 1);
        private int _localReleaseCompleted;
        private int _releaseCompleted;

        public LocalGateStreamLockHandle(
            SemaphoreSlim semaphore,
            IStreamLockHandle inner,
            ILogger logger,
            Guid conversationId)
        {
            _semaphore = semaphore;
            _inner = inner;
            _logger = logger;
            _conversationId = conversationId;
        }

        public bool ConversationLockEventSent => _inner.ConversationLockEventSent;

        public Guid LeaseId => _inner.LeaseId;

        public CancellationToken LeaseLostToken => _inner.LeaseLostToken;

        public void BeginStreamingRenewal() => _inner.BeginStreamingRenewal();

        public async Task<bool> ReleaseAsync(CancellationToken ct)
        {
            await _releaseGate.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref _releaseCompleted) == 1)
                {
                    return false;
                }

                if (Volatile.Read(ref _localReleaseCompleted) == 0)
                {
                    try
                    {
                        _semaphore.Release();
                        Interlocked.Exchange(ref _localReleaseCompleted, 1);
                    }
                    catch (SemaphoreFullException)
                    {
                        // The gate is already available. Treat this as an idempotent release so
                        // a distributed-release retry cannot strand the local gate.
                        Interlocked.Exchange(ref _localReleaseCompleted, 1);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", _conversationId);
                        return false;
                    }
                }

                if (!await _inner.ReleaseAsync(ct))
                {
                    return false;
                }

                Interlocked.Exchange(ref _releaseCompleted, 1);
                return true;
            }
            finally
            {
                _releaseGate.Release();
            }
        }
    }
}
