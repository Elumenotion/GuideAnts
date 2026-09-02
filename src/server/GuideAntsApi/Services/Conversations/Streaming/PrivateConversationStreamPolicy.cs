using System.Collections.Concurrent;
using System.Text.Json;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class PrivateConversationStreamPolicy : ConversationStreamPolicyBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationBroadcastHub _broadcastHub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<Guid, LocalGateState> _conversationLocks = new();

    private sealed class LocalGateState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object SyncRoot { get; } = new();
        public int PendingAcquisitions { get; set; }
    }

    public PrivateConversationStreamPolicy(
        IConversationBroadcastHub broadcastHub,
        ConversationStreamLockCoordinator lockCoordinator,
        IServiceScopeFactory scopeFactory,
        ILogger<PrivateConversationStreamPolicy> logger)
        : base(lockCoordinator, logger)
    {
        _broadcastHub = broadcastHub;
        _scopeFactory = scopeFactory;
    }

    public override ConversationUsageMode UsageMode => ConversationUsageMode.Private;

    public override bool SupportsExternalToolResume => false;

    public override bool UsesProgressThrottling => true;

    public override async Task<StreamUserIdentity> ResolveUserIdentityAsync(Guid? internalUserId, string? externalUserIdentity, CancellationToken ct)
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

    public override string SanitizeAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.SanitizePrivateAssistantContent(content, filenameUrlMap, ctx.HostUrl);

    public override string SanitizeToolContent(string content, ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.ConvertSandboxUrlsToRelative(content);

    public override void UpdateFilenameUrlMapFromToolMessage(
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

    public override Task OnTurnCreatedAsync(Guid conversationId, StreamTurnCreatedInfo info, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.TurnCreated, JsonSerializer.Serialize(new
            {
                turnId = info.TurnId,
                turnIndex = info.TurnIndex,
                userId = info.User.UserId ?? Guid.Empty,
                userName = info.User.UserName,
                userMessage = info.UserMessage,
                assistantName = info.AssistantName,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public override Task OnStreamingStartedAsync(Guid conversationId, StreamStreamingStartedInfo info, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.StreamingStarted, JsonSerializer.Serialize(new
            {
                assistantName = info.AssistantName,
                turnIndex = info.TurnIndex,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public override Task OnUnlockAsync(Guid conversationId, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.ConversationUnlocked, JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public override Task OnCompleteAsync(Guid conversationId, Guid turnId, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(
                StreamingEventTypes.Complete,
                JsonSerializer.Serialize(new { turnId }, JsonOptions)));

    public override Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
        Guid turnId,
        int contentLength,
        int tokensProcessed,
        CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.StreamingProgress, JsonSerializer.Serialize(new
            {
                turnId,
                userId = user.UserId ?? Guid.Empty,
                activeUserName = user.UserName,
                contentLength,
                tokensProcessed,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    public override Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId, ev);

    protected override bool ShouldEmitConversationLockEvent => true;

    protected override async Task<SemaphoreSlim?> TryAcquireLocalGateAsync(Guid conversationId, CancellationToken ct)
    {
        var state = _conversationLocks.GetOrAdd(conversationId, _ => new LocalGateState());
        lock (state.SyncRoot)
        {
            state.PendingAcquisitions++;
        }

        try
        {
            await state.Gate.WaitAsync(ct);
            return state.Gate;
        }
        catch
        {
            LocalGateAcquisitionResolved(conversationId);
            throw;
        }
    }

    protected override Task OnLockAcquiredAsync(Guid conversationId, StreamUserIdentity user, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.ConversationLocked, JsonSerializer.Serialize(new
            {
                activeUserId = user.UserId ?? Guid.Empty,
                activeUserName = user.UserName,
                timestamp = DateTime.UtcNow
            }, JsonOptions)));

    internal SemaphoreSlim GetOrCreateConversationGate(Guid conversationId) =>
        _conversationLocks.GetOrAdd(conversationId, _ => new LocalGateState()).Gate;

    internal SemaphoreSlim? GetConversationGate(Guid conversationId) =>
        _conversationLocks.TryGetValue(conversationId, out var state) ? state.Gate : null;

    /// <summary>
    /// Repairs a local gate only when no stream is currently waiting for distributed lock
    /// acquisition. The admission marker is synchronized with local gate acquisition so Stop
    /// cannot over-release a gate owned by a request between local and distributed locking.
    /// </summary>
    internal bool TryReleaseOrphanedConversationGate(Guid conversationId)
    {
        if (!_conversationLocks.TryGetValue(conversationId, out var state))
        {
            return true;
        }

        lock (state.SyncRoot)
        {
            if (state.PendingAcquisitions > 0)
            {
                return false;
            }

            if (state.Gate.CurrentCount == 0)
            {
                try
                {
                    state.Gate.Release();
                }
                catch (SemaphoreFullException)
                {
                    // The gate became available before this idempotent repair.
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Releases the local gate after the durable Stop transaction has fenced the previous worker.
    /// Unlike orphan repair, pending acquisitions are expected here: they are the requests that
    /// must be allowed to proceed after the old owner is revoked.
    /// </summary>
    internal bool TryReleaseFencedConversationGate(Guid conversationId)
    {
        if (!_conversationLocks.TryGetValue(conversationId, out var state))
        {
            return true;
        }

        lock (state.SyncRoot)
        {
            if (state.Gate.CurrentCount != 0)
            {
                return true;
            }

            try
            {
                state.Gate.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another lifecycle path released the gate concurrently. It is already open.
            }

            return true;
        }
    }

    protected override void LocalGateAcquisitionResolved(Guid conversationId)
    {
        if (!_conversationLocks.TryGetValue(conversationId, out var state))
        {
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.PendingAcquisitions > 0)
            {
                state.PendingAcquisitions--;
            }
        }
    }

}
