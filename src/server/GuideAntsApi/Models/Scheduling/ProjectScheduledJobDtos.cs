namespace GuideAntsApi.Models.Scheduling;

public enum ScheduleFrequency
{
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Custom
}

public record FriendlyScheduleDto(
    ScheduleFrequency Frequency,
    string? TimeOfDay,
    int[]? DaysOfWeek,
    int? DayOfMonth,
    int? HourlyIntervalMinutes,
    string? CustomCronExpression);

public record ScheduleValidationResult(
    bool IsValid,
    string? ErrorMessage,
    string? CronExpression);

public record ProjectScheduledJobSummaryDto(
    Guid Id,
    string Name,
    string JobType,
    Guid NotebookId,
    string NotebookTitle,
    bool IsEnabled,
    string CronExpression,
    string TimeZoneId,
    string ScheduleSummary,
    FriendlyScheduleDto FriendlySchedule,
    DateTime? NextRunUtc,
    DateTime? LastRunUtc,
    string? LastRunStatus,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public record ProjectScheduledJobDetailDto(
    Guid Id,
    string Name,
    string JobType,
    Guid NotebookId,
    string NotebookTitle,
    bool IsEnabled,
    string CronExpression,
    string TimeZoneId,
    string ScheduleSummary,
    FriendlyScheduleDto FriendlySchedule,
    string? ConversationTitle,
    string? Prompt,
    string? AssistantName,
    Guid? ScriptNotebookFileId,
    string? ScriptRelativePath,
    bool ExposeSandboxWireApi,
    Guid? WireTargetAssistantId,
    string? WireAttributionConversationTitle,
    bool WireCreateAttributionConversationPerRun,
    decimal? WireDailyLimitUsd,
    decimal? WireMonthlyLimitUsd,
    DateTime? NextRunUtc,
    DateTime? LastRunUtc,
    string? LastRunStatus,
    Guid CreatedByUserId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public record CreateProjectScheduledJobRequest(
    string Name,
    string JobType,
    Guid NotebookId,
    bool IsEnabled,
    string TimeZoneId,
    FriendlyScheduleDto Schedule,
    string? ConversationTitle,
    string? Prompt,
    string? AssistantName,
    Guid? ScriptNotebookFileId,
    bool ExposeSandboxWireApi = false,
    Guid? WireTargetAssistantId = null,
    string? WireAttributionConversationTitle = null,
    bool WireCreateAttributionConversationPerRun = false,
    decimal? WireDailyLimitUsd = null,
    decimal? WireMonthlyLimitUsd = null);

public record UpdateProjectScheduledJobRequest(
    string Name,
    string JobType,
    Guid NotebookId,
    bool IsEnabled,
    string TimeZoneId,
    FriendlyScheduleDto Schedule,
    string? ConversationTitle,
    string? Prompt,
    string? AssistantName,
    Guid? ScriptNotebookFileId,
    bool ExposeSandboxWireApi = false,
    Guid? WireTargetAssistantId = null,
    string? WireAttributionConversationTitle = null,
    bool WireCreateAttributionConversationPerRun = false,
    decimal? WireDailyLimitUsd = null,
    decimal? WireMonthlyLimitUsd = null);

public record ProjectScheduledJobRunSummaryDto(
    Guid Id,
    string TriggeredBy,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Status,
    string? ErrorMessage,
    Guid? CreatedConversationId,
    int? ExitCode);

public record ProjectScheduledJobRunDetailDto(
    Guid Id,
    string TriggeredBy,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Status,
    string? ErrorMessage,
    string? StandardOutput,
    string? StandardError,
    Guid? CreatedConversationId,
    int? ExitCode);

public record PagedProjectScheduledJobRunsDto(
    IReadOnlyList<ProjectScheduledJobRunSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);
