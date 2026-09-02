using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed record StreamUserIdentity(
    Guid? UserId,
    string UserName,
    string? ExternalUserIdentity);

public sealed record StreamTurnCreatedInfo(
    Guid TurnId,
    int TurnIndex,
    string UserMessage,
    string AssistantName,
    StreamUserIdentity User);

public sealed record StreamStreamingStartedInfo(
    string AssistantName,
    int TurnIndex);

public interface IStreamLockHandle
{
    Guid LeaseId { get; }

    bool ConversationLockEventSent { get; }

    /// <summary>
    /// Begin TTL renewal. Must not run during pre-stream setup; renewal while setup is hung
    /// keeps the conversation lock forever and makes Stop ineffective.
    /// </summary>
    void BeginStreamingRenewal();

    /// <summary>
    /// Reserved for explicit lease fencing. Slow or failed lock renewal does not cancel this token.
    /// </summary>
    CancellationToken LeaseLostToken { get; }

    Task<bool> ReleaseAsync(CancellationToken ct);
}

public interface IConversationStreamPolicy
{
    ConversationUsageMode UsageMode { get; }

    bool SupportsExternalToolResume { get; }

    bool UsesProgressThrottling { get; }

    Task<StreamUserIdentity> ResolveUserIdentityAsync(Guid? internalUserId, string? externalUserIdentity, CancellationToken ct);

    ConversationFileUrlContext BuildFileUrlContext(NotebookConversation conversation, string? publisherId, string? hostUrl);

    string SanitizeAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx);

    string SanitizeToolContent(string content, ConversationFileUrlContext ctx);

    void UpdateFilenameUrlMapFromToolMessage(
        string sanitizedToolContent,
        ConversationFileUrlContext ctx,
        IDictionary<string, string> filenameUrlMap,
        NotebookConversation conversation);

    Task<IStreamLockHandle> TryAcquireStreamAsync(
        Guid conversationId,
        StreamUserIdentity user,
        CancellationToken ct);

    Task OnTurnCreatedAsync(Guid conversationId, StreamTurnCreatedInfo info, CancellationToken ct);

    Task OnStreamingStartedAsync(Guid conversationId, StreamStreamingStartedInfo info, CancellationToken ct);

    Task OnUnlockAsync(Guid conversationId, CancellationToken ct);

    Task OnCompleteAsync(Guid conversationId, Guid turnId, CancellationToken ct);

    Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
        Guid turnId,
        int contentLength,
        int tokensProcessed,
        CancellationToken ct);

    Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct);
}
