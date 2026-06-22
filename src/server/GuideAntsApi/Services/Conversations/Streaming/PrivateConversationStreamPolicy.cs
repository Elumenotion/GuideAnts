using System.Collections.Concurrent;
using System.Text.Json;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class PrivateConversationStreamPolicy : IConversationStreamPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationBroadcastHub _broadcastHub;
    private readonly IDistributedConversationLock _distributedLock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PrivateConversationStreamPolicy> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _conversationLocks = new();

    public PrivateConversationStreamPolicy(
        IConversationBroadcastHub broadcastHub,
        IDistributedConversationLock distributedLock,
        IServiceScopeFactory scopeFactory,
        ILogger<PrivateConversationStreamPolicy> logger)
    {
        _broadcastHub = broadcastHub;
        _distributedLock = distributedLock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ConversationUsageMode UsageMode => ConversationUsageMode.Private;

    public bool SupportsExternalToolResume => false;

    public bool UsesProgressThrottling => true;

    public async Task<StreamUserIdentity> ResolveUserIdentityAsync(Guid? internalUserId, string? externalUserIdentity, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var currentUserService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        var currentUser = await currentUserService.GetCurrentUserAsync(ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Authenticated user is required.");

        var userName = string.IsNullOrWhiteSpace(currentUser.Name) ? currentUser.Email : currentUser.Name;
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new UnauthorizedAccessException("User identity could not be established for conversation streaming");
        }

        return new StreamUserIdentity(currentUser.UserId, userName, externalUserIdentity);
    }

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

    public string SanitizeAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.SanitizePrivateAssistantContent(content, filenameUrlMap, ctx.HostUrl);

    public string SanitizeToolContent(string content, ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.ConvertSandboxUrlsToRelative(content);

    public void UpdateFilenameUrlMapFromToolMessage(
        string sanitizedToolContent,
        ConversationFileUrlContext ctx,
        IDictionary<string, string> filenameUrlMap,
        NotebookConversation conversation)
    {
        foreach (var kv in AssistantContentSanitizer.ExtractPrivateFilenameUrlMapFromToolMessage(
                     sanitizedToolContent,
                     ctx))
        {
            filenameUrlMap[kv.Key] = kv.Value;
        }
    }

    public async Task<IStreamLockHandle> TryAcquireStreamAsync(
        Guid conversationId,
        StreamUserIdentity user,
        CancellationToken ct)
    {
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, user.UserName, ct);
        switch (lockResult.Status)
        {
            case LockAcquisitionStatus.ConversationNotFound:
                throw new KeyNotFoundException($"Conversation {conversationId} not found");
            case LockAcquisitionStatus.AlreadyLocked:
                throw new InvalidOperationException($"Conversation is locked by {lockResult.LockedByUserName}");
            case LockAcquisitionStatus.RaceCondition:
                throw new InvalidOperationException("Conversation is locked by another user");
        }

        var lockSemaphore = _conversationLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await lockSemaphore.WaitAsync(ct);

        await _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.ConversationLocked, JsonSerializer.Serialize(new
            {
                activeUserId = user.UserId ?? Guid.Empty,
                activeUserName = user.UserName,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

        return new PrivateStreamLockHandle(
            conversationId,
            lockSemaphore,
            _distributedLock,
            _broadcastHub,
            _logger,
            conversationLockEventSent: true);
    }

    public Task OnTurnCreatedAsync(Guid conversationId, StreamTurnCreatedInfo info, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.TurnCreated, JsonSerializer.Serialize(new
            {
                turnIndex = info.TurnIndex,
                userId = info.User.UserId ?? Guid.Empty,
                userName = info.User.UserName,
                userMessage = info.UserMessage,
                assistantName = info.AssistantName,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public Task OnStreamingStartedAsync(Guid conversationId, StreamStreamingStartedInfo info, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.StreamingStarted, JsonSerializer.Serialize(new
            {
                assistantName = info.AssistantName,
                turnIndex = info.TurnIndex,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public Task OnUnlockAsync(Guid conversationId, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.ConversationUnlocked, JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public Task OnCompleteAsync(Guid conversationId, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.Complete, "{}"));

    public Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
        int contentLength,
        int tokensProcessed,
        CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.StreamingProgress, JsonSerializer.Serialize(new
            {
                userId = user.UserId ?? Guid.Empty,
                activeUserName = user.UserName,
                contentLength,
                tokensProcessed,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId, ev);

    internal SemaphoreSlim GetOrCreateConversationGate(Guid conversationId) =>
        _conversationLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

    internal SemaphoreSlim? GetConversationGate(Guid conversationId) =>
        _conversationLocks.TryGetValue(conversationId, out var gate) ? gate : null;

    private sealed class PrivateStreamLockHandle : IStreamLockHandle
    {
        private readonly Guid _conversationId;
        private readonly SemaphoreSlim _semaphore;
        private readonly IDistributedConversationLock _distributedLock;
        private readonly IConversationBroadcastHub _broadcastHub;
        private readonly ILogger _logger;
        private bool _released;

        public PrivateStreamLockHandle(
            Guid conversationId,
            SemaphoreSlim semaphore,
            IDistributedConversationLock distributedLock,
            IConversationBroadcastHub broadcastHub,
            ILogger logger,
            bool conversationLockEventSent)
        {
            _conversationId = conversationId;
            _semaphore = semaphore;
            _distributedLock = distributedLock;
            _broadcastHub = broadcastHub;
            _logger = logger;
            ConversationLockEventSent = conversationLockEventSent;
        }

        public bool ConversationLockEventSent { get; }

        public async Task<bool> ReleaseAsync(CancellationToken ct)
        {
            if (_released)
            {
                return false;
            }

            _released = true;
            var distributedReleased = false;

            try
            {
                _semaphore.Release();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release local semaphore for {ConversationId}", _conversationId);
            }

            try
            {
                await _distributedLock.ReleaseLockAsync(_conversationId, ct);
                distributedReleased = true;
                _logger.LogInformation("Released conversation lock for {ConversationId}", _conversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to release distributed conversation lock for {ConversationId}", _conversationId);
            }

            return distributedReleased;
        }
    }
}
