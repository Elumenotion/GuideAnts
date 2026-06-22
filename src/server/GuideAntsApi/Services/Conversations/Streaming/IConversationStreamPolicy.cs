using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed record StreamUserIdentity(
    Guid? UserId,
    string UserName,
    string? ExternalUserIdentity);

public sealed record StreamTurnCreatedInfo(
    int TurnIndex,
    string UserMessage,
    string AssistantName,
    StreamUserIdentity User);

public sealed record StreamStreamingStartedInfo(
    string AssistantName,
    int TurnIndex);

public interface IStreamLockHandle
{
    bool ConversationLockEventSent { get; }

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

    Task OnCompleteAsync(Guid conversationId, CancellationToken ct);

    Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
        int contentLength,
        int tokensProcessed,
        CancellationToken ct);

    Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct);
}
