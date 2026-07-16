using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public abstract class ConversationStreamPolicyBase : IConversationStreamPolicy
{
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
            ReleaseLocalGate(localGate, conversationId);
            throw;
        }

        try
        {
            await OnLockAcquiredAsync(conversationId, user, ct);
        }
        catch
        {
            await distributedHandle.ReleaseAsync(CancellationToken.None);
            ReleaseLocalGate(localGate, conversationId);
            throw;
        }

        return localGate is null
            ? distributedHandle
            : new LocalGateStreamLockHandle(localGate, distributedHandle, _logger, conversationId);
    }

    public virtual Task OnTurnCreatedAsync(Guid conversationId, StreamTurnCreatedInfo info, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task OnStreamingStartedAsync(Guid conversationId, StreamStreamingStartedInfo info, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task OnUnlockAsync(Guid conversationId, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task OnCompleteAsync(Guid conversationId, CancellationToken ct) =>
        Task.CompletedTask;

    public virtual Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", conversationId);
        }
    }

    private sealed class LocalGateStreamLockHandle : IStreamLockHandle
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IStreamLockHandle _inner;
        private readonly ILogger _logger;
        private readonly Guid _conversationId;
        private bool _released;

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

        public async Task<bool> ReleaseAsync(CancellationToken ct)
        {
            if (_released)
            {
                return false;
            }

            _released = true;
            try
            {
                _semaphore.Release();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", _conversationId);
            }

            return await _inner.ReleaseAsync(ct);
        }
    }
}
