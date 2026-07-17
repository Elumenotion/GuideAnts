namespace AntRunner.Chat;

/// <summary>
/// Captures deterministic prompt-trace snapshots during a thread run.
/// Implementations must be side-effect free for execution semantics.
/// </summary>
public interface IThreadRunTraceCollector
{
    void CaptureSeedMessages(IReadOnlyList<ThreadRunTraceMessageSnapshot> messages);

    void CaptureToolDefinitions(IReadOnlyList<ThreadRunTraceToolDefinitionSnapshot> tools);

    void CaptureRoundRequest(
        int roundIndex,
        string? modelDeploymentId,
        IReadOnlyList<ThreadRunTraceMessageSnapshot> requestMessages,
        IReadOnlyList<ThreadRunTraceToolDefinitionSnapshot> tools);

    void CaptureRoundResponse(
        int roundIndex,
        string? finishReason,
        ThreadRunTraceMessageSnapshot responseMessage);

    void CaptureExternalToolCalls(
        int roundIndex,
        IReadOnlyList<ThreadRunTraceToolCallSnapshot> toolCalls);

    void CaptureMessageEvent(
        string role,
        string? content,
        string? toolCallId,
        string? functionName,
        string? toolCallsJson);

    void CaptureTerminalStatus(string status, string? errorMessage = null);

    void CaptureToolLimitState(
        int toolCallsUsed,
        string escalationPhase);
}

public sealed record ThreadRunTraceMessageSnapshot(
    string Role,
    string? Content,
    string? ToolCallId,
    string? FunctionName,
    string? ToolCallsJson);

public sealed record ThreadRunTraceToolDefinitionSnapshot(
    string Name,
    string? Description,
    string? ParametersJson,
    string Source);

public sealed record ThreadRunTraceToolCallSnapshot(
    string Id,
    string Name,
    string? ArgumentsJson);
