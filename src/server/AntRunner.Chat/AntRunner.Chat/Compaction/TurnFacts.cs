namespace AntRunner.Chat.Compaction;

/// <summary>
/// Provider-neutral, already-materialized projection of one turn's file activity. The caller
/// (the history builder, W5) loads these from persisted <c>ConversationTurn.FilesCreated</c> /
/// <c>FilesModified</c> - the engine performs no JSON parsing or DB access of its own.
/// Pass only the turns that fall before the compaction boundary - the engine does no boundary
/// filtering of its own; a caller that includes post-boundary turns will silently over-report
/// both the Artifacts section and <see cref="CompactionResult.SummarizedTurnCount"/>.
/// </summary>
public sealed record TurnFacts(
    int TurnIndex,
    IReadOnlyList<string> FilesCreated,
    IReadOnlyList<string> FilesModified);
