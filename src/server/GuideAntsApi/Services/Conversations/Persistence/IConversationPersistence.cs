using AntRunner.Chat;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations.Persistence;

public sealed record CreateTurnRequest(
    Guid ConversationId,
    string AssistantName,
    string? ModelDeploymentId,
    string Instructions,
    string? InitialStatus = null);

public sealed record CreatedTurnResult(int TurnIndex, Guid TurnId, ConversationTurn Turn);

public sealed record CreateUserMessageRequest(
    Guid ConversationId,
    int TurnIndex,
    int MessageSequence,
    string Content,
    string? ModelDeploymentId,
    Guid? UserId,
    string? ExternalUserIdentity,
    Guid? AssistantId,
    string AssistantName = "user");

public sealed record CreatedUserMessageResult(Guid MessageId, NotebookConversationMessage Message);

public sealed record StartAssistantMessageRequest(
    Guid ConversationId,
    Guid TurnId,
    int TurnIndex,
    int MessageSequence,
    string AssistantName,
    string? ModelDeploymentId,
    Guid? AssistantId,
    string Content = "",
    bool IsStreaming = true,
    string? ToolCallsJson = null);

public sealed record AssistantMessageUpdateRequest(
    Guid MessageId,
    Guid TurnId,
    string Content,
    bool Finalize,
    string? ToolCallsJson = null,
    string? ThinkingBlocksJson = null);

public sealed record CreateToolMessageRequest(
    Guid ConversationId,
    Guid TurnId,
    int TurnIndex,
    int MessageSequence,
    string Content,
    string? ToolCallId,
    string? FunctionName,
    Guid? AssistantId,
    string? AssistantName);

/// <summary>
/// Result of creating or replacing a tool message.
/// </summary>
/// <param name="MessageId">Persisted message id (existing row when updated in place).</param>
/// <param name="Created">
/// True when a new row was inserted. False when an existing tool row for the same
/// <c>ToolCallId</c> was updated in place (and any duplicate rows removed).
/// </param>
public sealed record CreateToolMessageResult(Guid MessageId, bool Created);

public sealed record AppendTurnTraceSegmentRequest(
    Guid TurnId,
    Guid ConversationId,
    int TurnIndex,
    int SchemaVersion,
    string CaptureState,
    string SegmentJson);

public interface IConversationPersistence
{
    Task<CreatedTurnResult> CreateTurnAsync(CreateTurnRequest request, int turnIndex, CancellationToken ct = default);

    Task<CreatedTurnResult> CreateNextTurnAsync(CreateTurnRequest request, CancellationToken ct = default);

    Task<CreatedUserMessageResult> CreateUserMessageAsync(CreateUserMessageRequest request, CancellationToken ct = default);

    Task<bool> SetTurnStatusAsync(Guid turnId, string status, string? onlyIfCurrentStatus = null, CancellationToken ct = default);

    Task<Guid> StartAssistantMessageAsync(StartAssistantMessageRequest request, CancellationToken ct = default);

    Task AppendOrFinalizeAssistantMessageAsync(AssistantMessageUpdateRequest request, CancellationToken ct = default);

    Task FinalizeStreamingAssistantMessageIfStillStreamingAsync(
        Guid messageId,
        Guid turnId,
        string content,
        CancellationToken ct = default);

    Task<CreateToolMessageResult> CreateToolMessageAsync(CreateToolMessageRequest request, CancellationToken ct = default);

    Task PersistRunOutputAsync(Guid turnId, ChatRunOutput? output, CancellationToken ct = default);

    Task PruneIncompleteToolCallsAsync(Guid conversationId, int turnIndex, CancellationToken ct = default);

    Task PersistThinkingBlocksAsync(
        ChatRunOutput? output,
        IReadOnlyList<Guid> assistantMessageIds,
        CancellationToken ct = default);

    Task AppendTurnTraceSegmentAsync(AppendTurnTraceSegmentRequest request, CancellationToken ct = default);
}
