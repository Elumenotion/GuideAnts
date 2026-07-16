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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _conversationLocks = new();

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

    public override Task OnCompleteAsync(Guid conversationId, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.Complete, "{}"));

    public override Task BroadcastStreamingProgressAsync(
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

    public override Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct) =>
        _broadcastHub.BroadcastToConversationAsync(conversationId, ev);

    protected override bool ShouldEmitConversationLockEvent => true;

    protected override async Task<SemaphoreSlim?> TryAcquireLocalGateAsync(Guid conversationId, CancellationToken ct)
    {
        var lockSemaphore = _conversationLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await lockSemaphore.WaitAsync(ct);
        return lockSemaphore;
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
        _conversationLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

    internal SemaphoreSlim? GetConversationGate(Guid conversationId) =>
        _conversationLocks.TryGetValue(conversationId, out var gate) ? gate : null;

}
