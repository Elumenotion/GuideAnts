using AntRunner.ToolCalling.AssistantDefinitions;

namespace AntRunner.ToolCalling;

public enum LimitEscalationPhase
{
    None,
    SoftBlocked,
    ToolChoiceNone,
    ForceCompleted,
}

public sealed record ToolLimitState(
    int? MaxToolCalls,
    int? MaxToolRounds,
    int ToolCallsUsed,
    int ToolRoundsUsed,
    LimitEscalationPhase Phase)
{
    public static ToolLimitState FromAssistantDefinition(AssistantDefinition assistant) =>
        new(
            assistant.MaxToolCallsPerTurn,
            assistant.MaxToolRoundsPerTurn,
            ToolCallsUsed: 0,
            ToolRoundsUsed: 0,
            Phase: LimitEscalationPhase.None);

    public static ToolLimitState ForNestedInvoke(
        ToolLimitState? parent,
        int? childMaxToolCalls,
        int? childMaxToolRounds,
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
            childMaxToolRounds,
            ToolCallsUsed: 0,
            ToolRoundsUsed: 0,
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

    public bool HasExceededToolRounds() =>
        MaxToolRounds.HasValue && ToolRoundsUsed > MaxToolRounds.Value;

    public ToolLimitState AddToolCalls(int count) =>
        this with { ToolCallsUsed = ToolCallsUsed + count };

    public ToolLimitState AddToolRound() =>
        this with { ToolRoundsUsed = ToolRoundsUsed + 1 };

    public string BuildLimitToolResultMessage() =>
        MaxToolCalls.HasValue
            ? $"[Tool call limit reached ({ToolCallsUsed}/{MaxToolCalls.Value} configured for this assistant). " +
              "No additional tool calls are permitted for this turn. " +
              "Summarize what you have gathered and respond to the user.]"
            : "[Tool call limit reached. No additional tool calls are permitted for this turn. " +
              "Summarize what you have gathered and respond to the user.]";

    public string BuildRuntimeOverrideSystemMessage() =>
        "[Runtime: Tool call limit reached. Ignore prior instructions to retry tool calls for this turn.]";

    public static string BuildForceCompleteAssistantMessage() =>
        "I have reached the maximum number of tool calls allowed for this turn and cannot run additional tools. " +
        "Please review the tool results above for the information gathered so far.";
}
