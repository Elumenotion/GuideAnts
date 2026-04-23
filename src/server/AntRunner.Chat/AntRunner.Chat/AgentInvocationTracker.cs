using Microsoft.Extensions.Logging;
using GuideAnts.Usage;
using GuideAntsApi.DataModel.Models;

namespace AntRunner.Chat;

/// <summary>
/// Static tracker for agent invocation threads.
/// Mirrors the ChatUsage pattern - wraps GuideAnts.Usage recorder for use in static contexts.
/// </summary>
public static class AgentInvocationTracker
{
    private static readonly IAgentInvocationRecorderSync Recorder;

    static AgentInvocationTracker()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        Recorder = AgentInvocationRecorderFactory.CreateFromEnv(loggerFactory);
    }

    /// <summary>
    /// Creates a new agent invocation record at the start of an invocation.
    /// </summary>
    public static Guid StartInvocation(
        Guid parentConversationId,
        int parentTurnIndex,
        string? triggeringToolCallId,
        Guid? parentInvocationId,
        Guid assistantId,
        string assistantName,
        string? modelDeploymentId,
        string instructions,
        string? contextMessageJson,
        string? evaluator,
        int depth)
    {
        var parameters = new AgentInvocationParams(
            ParentConversationId: parentConversationId,
            ParentTurnIndex: parentTurnIndex,
            TriggeringToolCallId: triggeringToolCallId,
            ParentInvocationId: parentInvocationId,
            AssistantId: assistantId,
            AssistantName: assistantName,
            ModelDeploymentId: modelDeploymentId,
            Instructions: instructions,
            ContextMessageJson: contextMessageJson,
            Evaluator: evaluator,
            Depth: depth);

        return Recorder.CreateInvocation(parameters);
    }

    /// <summary>
    /// Records a message in an agent invocation thread.
    /// </summary>
    public static void RecordMessage(
        Guid invocationId,
        int sequence,
        ChatRole role,
        string content,
        string? toolCallsJson = null,
        string? functionName = null,
        string? toolCallId = null)
    {
        var parameters = new AgentInvocationMessageParams(
            InvocationId: invocationId,
            Sequence: sequence,
            Role: role,
            Content: content,
            ToolCallsJson: toolCallsJson,
            FunctionName: functionName,
            ToolCallId: toolCallId);

        Recorder.AddMessage(parameters);
    }

    /// <summary>
    /// Marks an invocation as completed with final metrics.
    /// </summary>
    public static void FinishInvocation(
        Guid invocationId,
        string status,
        string? errorMessage,
        string? usageJson,
        int llmRoundTrips,
        int toolCallCount,
        long durationMs)
    {
        var parameters = new AgentInvocationCompletionParams(
            InvocationId: invocationId,
            Status: status,
            ErrorMessage: errorMessage,
            UsageJson: usageJson,
            LlmRoundTrips: llmRoundTrips,
            ToolCallCount: toolCallCount,
            DurationMs: durationMs);

        Recorder.CompleteInvocation(parameters);
    }
}

