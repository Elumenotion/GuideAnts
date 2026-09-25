namespace GuideAntsApi.Models.Conversations;

/// <summary>
/// Result of a POST compact call.
/// </summary>
/// <param name="BoundaryTurnIndex">The conversation's compaction boundary after this call. Null if the
/// conversation has never had any complete turn.</param>
/// <param name="MessagesSummarized">Count of raw input messages fed into compaction for turns at or below
/// the boundary.</param>
/// <param name="EstimatedTokensBefore">Estimated token count of the whole conversation as it stands, verbatim
/// - not netted against any prior compaction. If the conversation was already compacted and has since grown,
/// this reports the size as if never compacted, not the size currently in effect.</param>
/// <param name="EstimatedTokensAfter">Estimated token count if this compaction is applied (summary plus the
/// post-boundary tail).</param>
public sealed record CompactionResultDto(
    int? BoundaryTurnIndex,
    int MessagesSummarized,
    int? EstimatedTokensBefore,
    int? EstimatedTokensAfter);
