using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat.Compaction;

/// <summary>
/// Deterministic outcome of compacting the messages before a user-chosen boundary. The caller
/// (W5) wraps <see cref="SummaryText"/> in a <c>[system: ...]</c> message and appends the
/// verbatim post-boundary tail after it.
/// </summary>
/// <param name="SummaryText">The rendered handoff briefing (Goal, Artifacts, Activity ledger,
/// Unresolved errors, Directives sections).</param>
/// <param name="SummarizedMessageCount">The raw INPUT <c>ChatMessage</c> count
/// (<c>preBoundaryMessages.Count</c>), including messages the internal noise filter later
/// discards - not the filtered block count. This is what the compact endpoint (W4) returns to
/// API clients as <c>messagesSummarized</c>.</param>
/// <param name="SummarizedTurnCount">The count of distinct <see cref="TurnFacts.TurnIndex"/>
/// values across the supplied <see cref="TurnFacts"/>.</param>
public sealed record CompactionResult(string SummaryText, int SummarizedMessageCount, int SummarizedTurnCount);

/// <summary>
/// Pure, deterministic compaction of everything before a user-chosen boundary into a fixed,
/// protocol-derived section vocabulary (Goal, Artifacts, Activity ledger, Unresolved errors,
/// Directives). No I/O, no DB, no vendor knowledge, no LLM call - see
/// docs/superpowers/specs/2026-09-21-context-compaction-design.md, Decisions D2/D3/D6.
/// </summary>
public static class CompactionEngine
{
    /// <summary>
    /// Compacts everything before a user-chosen boundary into a deterministic handoff summary.
    /// </summary>
    /// <param name="preBoundaryMessages">The messages before the compaction boundary, in
    /// chronological order. Order is load-bearing: <see cref="GoalExtractor"/> takes the first
    /// user-message block as the initial goal, and <see cref="UnresolvedErrorsExtractor"/> only
    /// resolves an error via a later entry in the list. May be null, which is treated as empty.
    /// </param>
    /// <param name="turnFacts">Per-turn file-activity facts for turns before the compaction
    /// boundary (see <see cref="TurnFacts"/> for the caller's boundary-scoping obligation). May
    /// be null, which is treated as empty.</param>
    /// <returns>A deterministic <see cref="CompactionResult"/>. Never throws on null or
    /// otherwise degenerate input.</returns>
    public static CompactionResult Compact(
        IReadOnlyList<ChatMessage> preBoundaryMessages,
        IReadOnlyList<TurnFacts> turnFacts)
    {
        preBoundaryMessages ??= [];
        turnFacts ??= [];

        var blocks = MessageNormalizer.Normalize(preBoundaryMessages);
        var filtered = NoiseFilter.Filter(blocks);

        var goal = GoalExtractor.Extract(filtered);
        var artifacts = ArtifactsExtractor.Extract(turnFacts);
        var activityLedger = ActivityLedgerExtractor.Extract(filtered);
        var unresolvedErrors = UnresolvedErrorsExtractor.Extract(activityLedger);
        var directives = DirectivesExtractor.Extract(filtered);

        var summaryText = CompactionSummaryRenderer.Render(
            goal, artifacts, activityLedger, unresolvedErrors, directives, preBoundaryMessages.Count);

        var summarizedTurnCount = turnFacts.Select(t => t.TurnIndex).Distinct().Count();

        return new CompactionResult(summaryText, preBoundaryMessages.Count, summarizedTurnCount);
    }
}
