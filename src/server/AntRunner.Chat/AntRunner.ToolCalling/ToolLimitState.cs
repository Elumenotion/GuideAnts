using AntRunner.ToolCalling.AssistantDefinitions;

namespace AntRunner.ToolCalling;

public enum LimitEscalationPhase
{
    None,
    SoftBlocked,
    ToolChoiceNone,
    ForceCompleted,
}

[Flags]
public enum ToolLimitHitKind
{
    None = 0,
    ToolCalls = 1,
}

public sealed record ToolLimitState(
    int? MaxToolCalls,
    int ToolCallsUsed,
    LimitEscalationPhase Phase,
    ToolLimitHitKind LastHitKind = ToolLimitHitKind.None)
{
    public const string RuntimeOverrideMarker = "[Runtime:";

    public static ToolLimitState FromAssistantDefinition(AssistantDefinition assistant) =>
        new(
            assistant.MaxToolCallsPerTurn,
            ToolCallsUsed: 0,
            Phase: LimitEscalationPhase.None);

    public static ToolLimitState ForNestedInvoke(
        ToolLimitState? parent,
        int? childMaxToolCalls,
        int? memberOverride)
    {
        int? effectiveMax = memberOverride ?? childMaxToolCalls;
        if (parent?.MaxToolCalls is int parentMax)
        {
            var parentRemaining = parentMax - parent.ToolCallsUsed - 1;
            if (parentRemaining < 0)
            {
                parentRemaining = 0;
            }

            effectiveMax = effectiveMax.HasValue
                ? Math.Min(effectiveMax.Value, parentRemaining)
                : parentRemaining;
        }

        return new ToolLimitState(
            effectiveMax,
            ToolCallsUsed: 0,
            Phase: LimitEscalationPhase.None);
    }

    public bool WouldExceedToolCalls(int additionalCalls)
    {
        if (!MaxToolCalls.HasValue || additionalCalls <= 0)
        {
            return false;
        }

        return ToolCallsUsed + additionalCalls > MaxToolCalls.Value;
    }

    public ToolLimitHitKind EvaluateLimitHit(int pendingCalls)
    {
        return WouldExceedToolCalls(pendingCalls)
            ? ToolLimitHitKind.ToolCalls
            : ToolLimitHitKind.None;
    }

    public ToolLimitState AddToolCalls(int count) =>
        this with { ToolCallsUsed = ToolCallsUsed + count };

    public string BuildLimitToolResultMessage(int pendingCalls)
    {
        var hit = EvaluateLimitHit(pendingCalls);
        var summary = DescribeLimitHit(hit, pendingCalls);
        return $"[{summary} No additional tools may run for this turn. " +
               "Summarize what you have gathered and respond to the user.]";
    }

    public string BuildRuntimeOverrideSystemMessage() =>
        $"{RuntimeOverrideMarker} {DescribeActiveLimitHit(LastHitKind, pastTense: true)} " +
        "Ignore prior instructions to retry tool calls for this turn.]";

    public string BuildSystemNudgeMessage(ToolLimitHitKind hit) =>
        $"[System: {DescribeActiveLimitHit(hit, pastTense: true)} " +
        "Summarize what you have gathered and respond to the user. Do not request additional tool calls.]";

    public static string BuildForceCompleteAssistantMessage(ToolLimitHitKind hitKind)
    {
        return "I have reached the configured tool execution limit for this turn and cannot run additional tools. " +
               "Please review the tool results above for the information gathered so far.";
    }

    public static string FormatLimitHitKinds(ToolLimitHitKind hitKind) =>
        hitKind switch
        {
            ToolLimitHitKind.ToolCalls => "ToolCalls",
            _ => string.Empty,
        };

    private string DescribeActiveLimitHit(ToolLimitHitKind hit, bool pastTense)
    {
        var verb = pastTense ? "was reached" : "reached";
        return hit switch
        {
            ToolLimitHitKind.ToolCalls => $"Tool execution limit {verb} for this turn.",
            _ => $"Execution limit {verb} for this turn.",
        };
    }

    private string DescribeLimitHit(ToolLimitHitKind hit, int pendingCalls)
    {
        return hit switch
        {
            ToolLimitHitKind.ToolCalls => DescribeToolCallLimit(pendingCalls),
            _ => "Execution limit reached for this turn.",
        };
    }

    private string DescribeToolCallLimit(int pendingCalls, bool sentence = true)
    {
        if (!MaxToolCalls.HasValue)
        {
            return sentence
                ? "Tool execution limit reached for this turn."
                : "tool execution limit reached";
        }

        var projected = ToolCallsUsed + Math.Max(pendingCalls, 0);
        var detail = $"tools {projected}/{MaxToolCalls.Value}";
        return sentence
            ? $"Tool execution limit reached ({detail} configured for this assistant)."
            : detail;
    }

}
