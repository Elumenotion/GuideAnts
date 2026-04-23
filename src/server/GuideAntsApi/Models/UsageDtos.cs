using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.Models;

public enum UsageBucketDto
{
    Day,
    Week,
    Month
}

public sealed record UsageTimePointDto(
    DateTime PeriodStart,
    long TotalEvents,
    decimal TotalCostUsd
);

public sealed record UsageCategoryTotalsDto(
    string Category,
    long TotalEvents,
    decimal TotalCostUsd
);

public sealed record UsageSummaryDto(
    DateTime From,
    DateTime To,
    UsageBucketDto Bucket,
    UsageTotals Totals,
    IReadOnlyList<UsageTimePointDto> TimeSeries,
    IReadOnlyList<UsageCategoryTotalsDto> ByCategory
);

public sealed record UsageTotals(long TotalEvents, decimal TotalCostUsd);

public sealed record ProjectUsageSummaryDto(
    Guid ProjectId,
    string ProjectTitle,
    long TotalEvents,
    decimal TotalCostUsd
);

public sealed record UsageEventDto(
    Guid Id,
    DateTime Created,
    Guid? ProjectId,
    Guid? NotebookId,
    Guid? ConversationId,
    Guid? ContentFileId,
    Guid? NotebookFileId,
    string Category,
    string Service,
    string Operation,
    string? ModelDeploymentId,
    long ValueInput,
    long ValueCachedInput,
    long ValueReasoning,
    long ValueOutput,
    long ValueOther,
    string? MetadataJson,
    decimal? CostUsd
);

public sealed class UsageDetailsQueryDto
{
    [Required]
    public DateTime From { get; set; }
    [Required]
    public DateTime To { get; set; }
    public Guid? ProjectId { get; set; }
    public string? Category { get; set; }
    public string? Service { get; set; }
    public string? Operation { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    long Total,
    int Page,
    int PageSize
);

public sealed record ServiceCostDto(
    string Service,
    long TotalEvents,
    decimal TotalCostUsd
);

public sealed record OperationCostDto(
    string Service,
    string Operation,
    long TotalEvents,
    decimal TotalCostUsd
);

public sealed record UsageBreakdownDto(
    IReadOnlyList<ServiceCostDto> ByService,
    IReadOnlyList<OperationCostDto> ByOperation
);

public sealed record ReportCategoryDto(
    Guid Id,
    string Key,
    string Title,
    string? Description,
    long TotalEvents,
    decimal TotalCostUsd
);

public sealed record UsageBreakdownWithCategoriesDto(
    IReadOnlyList<ServiceCostDto> ByService,
    IReadOnlyList<OperationCostDto> ByOperation,
    IReadOnlyList<ReportCategoryDto> ByCategory
);


