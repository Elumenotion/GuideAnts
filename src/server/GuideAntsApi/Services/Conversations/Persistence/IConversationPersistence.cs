using AntRunner.Chat;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations.Persistence;

public sealed record CreateTurnRequest(
    Guid ConversationId,
    string AssistantName,
    string? ModelDeploymentId,
    string Instructions,
    string? InitialStatus = null,
    Guid? ExecutionId = null);

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
    string? ToolCallsJson = null,
    Guid? ExpectedExecutionId = null);

public sealed record AssistantMessageUpdateRequest(
    Guid MessageId,
    Guid TurnId,
    string Content,
    bool Finalize,
    string? ToolCallsJson = null,
    string? ThinkingBlocksJson = null,
    Guid? ExpectedExecutionId = null);

public sealed record CreateToolMessageRequest(
    Guid ConversationId,
    Guid TurnId,
    int TurnIndex,
    int MessageSequence,
    string Content,
    string? ToolCallId,
    string? FunctionName,
    Guid? AssistantId,
    string? AssistantName,
    Guid? ExpectedExecutionId = null);

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
    string SegmentJson,
    Guid? ExpectedExecutionId = null);

public sealed record TerminalizeAssistantSnapshot(
    Guid MessageId,
    string Content,
    string? ToolCallsJson = null,
    string? ThinkingBlocksJson = null);

public sealed record TerminalizeTurnRequest(
    Guid TurnId,
    Guid ConversationId,
    int TurnIndex,
    string TerminalStatus,
    string? TerminationCode = null,
    string? TerminationDetail = null,
    Guid? ExecutionId = null,
    ChatRunOutput? Output = null,
    IReadOnlyList<TerminalizeAssistantSnapshot>? AssistantSnapshots = null,
    bool PruneIncompleteToolCalls = false,
    IReadOnlyList<Guid>? AssistantMessageIdsForThinking = null);

/// <summary>
/// Result of the authoritative Stop transition. The turn is fenced before the old worker is
/// allowed to lose the conversation lock, so a replacement turn cannot be affected by late work.
/// </summary>
public sealed record FencedTurnCancellationResult(
    bool Found,
    bool WasStreaming,
    Guid? PreviousExecutionId,
    Guid? FencedExecutionId,
    bool PreviousLeaseWasReleased,
    string? Status,
    bool ConflictingLeasePresent = false,
    bool WasPendingClientTool = false);

/// <summary>
/// Raised when a stream attempts to write after its execution generation has been revoked.
/// This is an expected hard-stop boundary, not a provider failure.
/// </summary>
public sealed class ConversationTurnExecutionFencedException(Guid turnId)
    : InvalidOperationException($"Stream execution is no longer allowed to write turn {turnId}.")
{
    public Guid TurnId { get; } = turnId;
}

public interface IConversationPersistence
{
    Task<CreatedTurnResult> CreateTurnAsync(CreateTurnRequest request, int turnIndex, CancellationToken ct = default);

    Task<CreatedTurnResult> CreateNextTurnAsync(CreateTurnRequest request, CancellationToken ct = default);

    Task<CreatedUserMessageResult> CreateUserMessageAsync(CreateUserMessageRequest request, CancellationToken ct = default);

    Task<bool> SetTurnStatusAsync(Guid turnId, string status, string? onlyIfCurrentStatus = null, CancellationToken ct = default);

    /// <summary>
    /// Records a durable cancellation request without terminalizing a still-running turn.
    /// This lets a Stop request routed to a different API instance signal the worker that owns
    /// the in-process cancellation token.
    /// </summary>
    Task<bool> RequestTurnCancellationAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically marks a streaming turn cancelled, advances its execution fence, finalizes
    /// streaming assistant rows without deleting content, and removes only the lock owned by the
    /// previous execution. This is the logical Stop boundary; it never waits for the provider
    /// worker to exit.
    /// </summary>
    Task<FencedTurnCancellationResult> FenceTurnCancellationAsync(
        Guid conversationId,
        Guid turnId,
        Guid? expectedExecutionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Materializes cancellation tool-result rows for every persisted function call that does not
    /// yet have a matching tool message. Idempotent and safe to call after Stop fences or a
    /// tool-call persistence race.
    /// </summary>
    Task<int> MaterializeMissingCancellationToolResultsAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken ct = default);

    /// <summary>
    /// Preserves an announced assistant tool-call message after Stop has fenced the turn.
    /// Updates the supplied row when it is still empty, appends a distinct row when that row
    /// already contains different tool calls, and is idempotent when the same calls are present.
    /// </summary>
    Task<bool> TryPreserveStoppedAssistantToolCallsAsync(
        Guid conversationId,
        Guid turnId,
        Guid? messageId,
        string? content,
        string toolCallsJson,
        Guid? assistantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the durable cancellation marker so a provider that returns normally after Stop
    /// cannot be recorded as a successful completion.
    /// </summary>
    Task<bool> IsTurnCancellationRequestedAsync(Guid turnId, CancellationToken ct = default);

    /// <summary>
    /// Returns true only while the supplied execution generation is still allowed to publish
    /// turn-owned side effects.
    /// </summary>
    Task<bool> IsTurnExecutionActiveAsync(
        Guid turnId,
        Guid expectedExecutionId,
        CancellationToken ct = default);

    Task<Guid> StartAssistantMessageAsync(StartAssistantMessageRequest request, CancellationToken ct = default);

    Task AppendOrFinalizeAssistantMessageAsync(AssistantMessageUpdateRequest request, CancellationToken ct = default);

    Task FinalizeStreamingAssistantMessageIfStillStreamingAsync(
        Guid messageId,
        Guid turnId,
        string content,
        CancellationToken ct = default,
        Guid? expectedExecutionId = null);

    Task<CreateToolMessageResult> CreateToolMessageAsync(CreateToolMessageRequest request, CancellationToken ct = default);

    Task PersistRunOutputAsync(Guid turnId, ChatRunOutput? output, CancellationToken ct = default);

    Task PruneIncompleteToolCallsAsync(Guid conversationId, int turnIndex, CancellationToken ct = default);

    Task PersistThinkingBlocksAsync(
        ChatRunOutput? output,
        IReadOnlyList<Guid> assistantMessageIds,
        CancellationToken ct = default);

    Task AppendTurnTraceSegmentAsync(AppendTurnTraceSegmentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Atomically finalizes a turn: streaming assistant rows, run output, usage/files, and terminal status.
    /// Idempotent when the turn is already in a terminal status.
    /// </summary>
    Task<bool> TerminalizeTurnAsync(TerminalizeTurnRequest request, CancellationToken ct = default);

    /// <summary>
    /// Persists bounded assistant text/thinking for an in-flight turn when checkpoint version advances.
    /// </summary>
    Task<bool> CheckpointTurnAsync(
        Guid turnId,
        Guid messageId,
        string content,
        string? thinkingBlocksJson,
        int checkpointVersion,
        CancellationToken ct = default,
        Guid? expectedExecutionId = null);
}
