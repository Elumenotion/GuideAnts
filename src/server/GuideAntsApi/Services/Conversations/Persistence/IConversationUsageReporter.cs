using GuideAnts.Usage;

namespace GuideAntsApi.Services.Conversations.Persistence;

public enum ConversationUsageMode
{
    Private,
    Published
}

public sealed record ChatCompletionUsageRequest(
    ConversationUsageMode Mode,
    Guid ProjectId,
    Guid NotebookId,
    Guid ConversationId,
    int TurnIndex,
    string? ModelDeploymentId,
    Guid? AssistantId,
    UsageMetrics Metrics,
    Guid? PreferredAssistantMessageId = null,
    IReadOnlyList<Guid>? AssistantMessageIds = null);

public sealed record ToolTurnUsageRequest(
    ConversationUsageMode Mode,
    Guid ProjectId,
    Guid NotebookId,
    Guid ConversationId,
    int TurnIndex,
    Guid? AssistantId,
    string? ContextLabel = null);

public sealed record CancelledTurnUsageRequest(
    ConversationUsageMode Mode,
    Guid ProjectId,
    Guid NotebookId,
    Guid ConversationId,
    int TurnIndex,
    string? ModelDeploymentId,
    Guid? AssistantId,
    Guid? PreferredAssistantMessageId = null,
    IReadOnlyList<Guid>? AssistantMessageIds = null,
    string? ContextLabel = null);

public interface IConversationUsageReporter
{
    Task RecordChatCompletionUsageAsync(ChatCompletionUsageRequest request, CancellationToken ct = default);

    Task RecordToolCallUsageForTurnAsync(ToolTurnUsageRequest request, CancellationToken ct = default);

    Task RecordCancelledTurnMarkerUsageAsync(CancelledTurnUsageRequest request, CancellationToken ct = default);
}
