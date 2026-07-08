namespace GuideAntsApi.Services.SandboxWireApi;

public sealed record SandboxWireExecutionGrant(
    Guid ExecutionId,
    Guid ProjectId,
    Guid NotebookId,
    Guid OwnerAssistantId,
    Guid TargetAssistantId,
    string TargetAssistantName,
    IReadOnlyList<string> AllowedEndpoints,
    Guid? AttributionConversationId,
    IReadOnlyList<Guid> AncestorAssistantIds,
    TimeSpan Lifetime,
    decimal? DailyLimitUsd = null,
    decimal? MonthlyLimitUsd = null);
