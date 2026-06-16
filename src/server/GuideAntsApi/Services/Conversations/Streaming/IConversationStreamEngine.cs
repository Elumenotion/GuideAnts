using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class ConversationStreamRunContext
{
    public required IConversationStreamPolicy Policy { get; init; }
    public required Guid ConversationId { get; init; }
    public required NotebookConversation Conversation { get; init; }
    public required ConversationTurn DbTurn { get; init; }
    public required int TurnIndex { get; init; }
    public required string AssistantName { get; init; }
    public required Guid? AssistantId { get; init; }
    public required string? ModelDeploymentId { get; init; }
    public required ChatRunOptions ChatOptions { get; init; }
    public required List<ChatMessage> PreviousMessages { get; init; }
    public Guid? UserMessageId { get; init; }
    public required StreamUserIdentity User { get; init; }
    public string? PublisherId { get; init; }
    public string? HostUrl { get; init; }
    public bool ResumeWithoutNewUserMessage { get; init; }
    public string? UsageContextLabel { get; init; }
    public int InitialMessageSequence { get; init; } = 2;
}

public interface IConversationStreamEngine
{
    /// <summary>
    /// Runs a single streaming turn end-to-end: starts the chat run, forwards/broadcasts every event,
    /// then in the correct order releases the conversation lock, broadcasts unlock, and emits the final
    /// completion event. The same orchestration serves both private and published conversations; the
    /// supplied <paramref name="lockHandle"/> is a no-op for paths that do not lock.
    /// </summary>
    IAsyncEnumerable<StreamingEvent> RunStreamAsync(
        ConversationStreamRunContext context,
        IStreamLockHandle lockHandle,
        CancellationToken externalCt);
}
