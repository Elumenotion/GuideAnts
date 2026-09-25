using AntRunner.ToolCalling;

namespace AntRunner.Chat.Compaction;

internal static class NoiseFilter
{
    private static readonly string ForceCompleteMessage =
        ToolLimitState.BuildForceCompleteAssistantMessage(ToolLimitHitKind.ToolCalls);

    private static readonly string SystemNudgeMarker =
        new ToolLimitState(null, 0, LimitEscalationPhase.None).BuildSystemNudgeMessage(ToolLimitHitKind.ToolCalls);

    public static List<CompactionBlock> Filter(IReadOnlyList<CompactionBlock> blocks)
    {
        var filtered = new List<CompactionBlock>();
        foreach (var block in blocks)
        {
            if (IsEmptyContentBlock(block) || IsToolLimitScaffolding(block))
            {
                continue;
            }

            filtered.Add(block);
        }

        return filtered;
    }

    private static bool IsEmptyContentBlock(CompactionBlock block) =>
        block.Kind is CompactionBlockKind.UserMessage or CompactionBlockKind.AssistantMessage
            or CompactionBlockKind.SystemMessage
        && string.IsNullOrWhiteSpace(block.Text);

    // Detects tool-limit scaffolding by deriving markers from ToolLimitState methods
    // (RuntimeOverrideMarker / BuildSystemNudgeMessage / BuildForceCompleteAssistantMessage)
    // so this filter can't drift out of sync if ThreadRun.cs's wording changes.
    private static bool IsToolLimitScaffolding(CompactionBlock block)
    {
        if (block.Kind == CompactionBlockKind.SystemMessage)
        {
            return block.Text.Contains(ToolLimitState.RuntimeOverrideMarker, StringComparison.Ordinal)
                   || block.Text.Contains(SystemNudgeMarker, StringComparison.OrdinalIgnoreCase);
        }

        if (block.Kind == CompactionBlockKind.AssistantMessage)
        {
            return string.Equals(block.Text, ForceCompleteMessage, StringComparison.Ordinal);
        }

        return false;
    }
}
