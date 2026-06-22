using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides;

/// <summary>
/// Service for querying guide/assistant usage statistics and invocation details.
/// </summary>
public interface IGuideUsageService
{
    /// <summary>
    /// Gets top-line summary metrics for a guide usage report.
    /// </summary>
    Task<GuideUsageSummaryDto?> GetGuideUsageSummaryAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to);

    /// <summary>
    /// Gets daily usage buckets for charts.
    /// </summary>
    Task<List<DailyUsageBucketDto>?> GetGuideUsageDailyBucketsAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to);

    /// <summary>
    /// Gets crew member and direct tool call usage.
    /// </summary>
    Task<GuideUsageCrewDto?> GetGuideUsageCrewAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to);

    /// <summary>
    /// Gets paged conversation summaries for drill-down.
    /// </summary>
    Task<GuideUsageConversationsPageDto?> GetGuideUsageConversationsAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to,
        int page,
        int pageSize);

    /// <summary>
    /// Gets grouped API usage for source-channel attributed events.
    /// </summary>
    Task<GuideApiUsageReportDto?> GetGuideApiUsageReportAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to,
        string? sourceFilter = null);

    /// <summary>
    /// Gets a single invocation with its full message history.
    /// Supports both AgentInvocation IDs and NotebookConversation IDs.
    /// </summary>
    Task<InvocationNodeDto?> GetInvocationAsync(
        Guid invocationId,
        bool includeMessages = true);

    /// <summary>
    /// Gets the full invocation tree for a conversation turn.
    /// </summary>
    Task<TurnInvocationTreeDto?> GetTurnInvocationsAsync(
        Guid conversationId,
        int turnIndex);

    /// <summary>
    /// Gets the full invocation trees for all turns in a conversation.
    /// More efficient than calling GetTurnInvocationsAsync for each turn.
    /// </summary>
    Task<List<TurnInvocationTreeDto>> GetAllTurnInvocationsAsync(
        Guid conversationId);

    /// <summary>
    /// Gets conversation messages for a turn with AgentInvocation links for drill-down.
    /// </summary>
    Task<TurnMessagesDto?> GetTurnMessagesAsync(
        Guid conversationId,
        int turnIndex);
}
