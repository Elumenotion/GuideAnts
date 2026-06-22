namespace GuideAntsApi.Models.Guides;

/// <summary>
/// Top-line summary usage metrics.
/// </summary>
public record GuideUsageSummaryDto(
    Guid GuideId,
    string GuideName,
    DateTime FromDate,
    DateTime ToDate,
    int TotalConversations,
    long TotalPromptTokens,
    long TotalCachedTokens,
    long TotalReasoningTokens,
    long TotalCompletionTokens,
    long TotalPromptTokensWithCrew,
    long TotalCachedTokensWithCrew,
    long TotalReasoningTokensWithCrew,
    long TotalCompletionTokensWithCrew,
    decimal DirectCost,
    decimal TotalCost
);

/// <summary>
/// Crew and direct tool-call usage section.
/// </summary>
public record GuideUsageCrewDto(
    List<CrewMemberUsageDto> CrewMembers,
    List<ToolUsageDto> DirectToolCalls
);

/// <summary>
/// Paged conversation summaries section.
/// </summary>
public record GuideUsageConversationsPageDto(
    int Page,
    int PageSize,
    int TotalCount,
    List<ConversationUsageSummaryDto> Items
);

/// <summary>
/// Aggregated API usage row grouped by source, endpoint, alias, provider mode, and status family.
/// </summary>
public record GuideApiUsageRowDto(
    string SourceChannel,
    string Endpoint,
    string Alias,
    string ProviderServiceMode,
    string StatusFamily,
    int Events,
    decimal ChargeUsd
);

/// <summary>
/// API usage report section for non-conversation and attributed conversation API traffic.
/// </summary>
public record GuideApiUsageReportDto(
    Guid GuideId,
    string GuideName,
    DateTime FromDate,
    DateTime ToDate,
    string SourceFilter,
    int TotalEvents,
    decimal TotalChargeUsd,
    List<GuideApiUsageRowDto> Rows
);

/// <summary>
/// Usage report for a Guide/Assistant showing crew member and tool call statistics.
/// </summary>
public record GuideUsageReportDto(
    Guid GuideId,
    string GuideName,
    DateTime FromDate,
    DateTime ToDate,
    int TotalConversations,
    // Tokens (guide's direct ChatCompletion only)
    long TotalPromptTokens,
    long TotalCachedTokens,
    long TotalReasoningTokens,
    long TotalCompletionTokens,
    // Tokens including crew/agent invocations (guide direct + all subtrees)
    long TotalPromptTokensWithCrew,
    long TotalCachedTokensWithCrew,
    long TotalReasoningTokensWithCrew,
    long TotalCompletionTokensWithCrew,
    // Costs
    decimal DirectCost,  // Guide's own usage (ChatCompletion + direct tool calls)
    decimal TotalCost,   // Direct + all crew invocation subtree costs
    // Crew and tool usage
    List<CrewMemberUsageDto> CrewMembers,
    List<ToolUsageDto> DirectToolCalls,
    // Chart data
    List<DailyUsageBucketDto> DailyBuckets,
    // Per-conversation aggregates for drill-down
    List<ConversationUsageSummaryDto> Conversations
);

/// <summary>
/// Daily aggregated usage for time series charts.
/// </summary>
public record DailyUsageBucketDto(
    DateTime Date,
    // Guide's direct ChatCompletion tokens only
    long PromptTokens,
    long CachedTokens,
    long ReasoningTokens,
    long CompletionTokens,
    // Guide direct + crew/agent invocation subtree tokens
    long PromptTokensWithCrew,
    long CachedTokensWithCrew,
    long ReasoningTokensWithCrew,
    long CompletionTokensWithCrew,
    decimal DirectCost,
    decimal TotalCost
);

/// <summary>
/// Summary usage statistics for a single conversation, used as the basis for
/// drill-down into invocation trees and detailed usage.
/// </summary>
public record ConversationUsageSummaryDto(
    Guid ConversationId,
    Guid NotebookId,
    string Title,
    DateTime Created,
    int TotalToolCalls,
    int AssistantSteps,
    long PromptTokens,
    long CachedTokens,
    long ReasoningTokens,
    long CompletionTokens,
    decimal TotalCost,
    IReadOnlyList<int> TurnIndices
);

/// <summary>
/// Usage statistics for a crew member within a guide's conversations.
/// </summary>
public record CrewMemberUsageDto(
    Guid AssistantId,
    string AssistantName,
    int TotalInvocations,
    double AverageInvocationsPerConversation,
    decimal TotalCost,
    decimal AverageCostPerConversation
);

/// <summary>
/// Usage statistics for a tool called directly by the guide.
/// </summary>
public record ToolUsageDto(
    string ToolName,
    int TotalCalls,
    double AverageCallsPerConversation,
    decimal TotalCost,
    decimal AverageCostPerConversation
);

/// <summary>
/// Full invocation node with tree structure for guideants visualization.
/// </summary>
public record InvocationNodeDto(
    Guid Id,
    Guid? ParentInvocationId,
    string? TriggeringToolCallId,
    string AssistantName,
    Guid? AssistantId,
    string? ModelDeploymentId,
    int Depth,
    string Status,
    long PromptTokens,
    long CompletionTokens,
    int ToolCallCount,
    int LlmRoundTrips,
    decimal ChargeUsd,
    DateTime Created,
    DateTime? Completed,
    long DurationMs,
    bool IsOutlier,
    List<InvocationNodeDto> Children,
    List<InvocationMessageDto>? Messages = null
);

/// <summary>
/// Message within an invocation for detail panel.
/// </summary>
public record InvocationMessageDto(
    Guid Id,
    string Role,
    string? Content,
    string? ToolCallsJson,
    string? FunctionName,
    DateTime Created
);

/// <summary>
/// Tree response for guideants view anchored to a conversation turn.
/// </summary>
public record TurnInvocationTreeDto(
    Guid ConversationId,
    int TurnIndex,
    DateTime TurnStarted,
    long TotalDurationMs,
    decimal TotalChargeUsd,
    long TotalTokens,
    List<InvocationNodeDto> RootInvocations
);

/// <summary>
/// Message in a conversation turn with optional AgentInvocation link for drill-down.
/// </summary>
public record TurnMessageDto(
    Guid Id,
    string Role,
    string? Content,
    string? ToolCallsJson,
    string? ToolCallId,
    string? FunctionName,
    DateTime Created,
    Guid? AgentInvocationId  // If this tool call triggered an agent invocation
);/// <summary>
/// Response containing conversation turn messages with invocation links.
/// </summary>
public record TurnMessagesDto(
    Guid ConversationId,
    int TurnIndex,
    string? AssistantName,
    DateTime TurnStarted,
    List<TurnMessageDto> Messages
);
