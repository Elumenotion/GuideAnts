namespace GuideAntsApi.Services.PublishedGuides;

public record PublishedGuideCostLimitResult(
    bool Allowed,
    string? Reason,
    decimal? DailyLimitUsd,
    decimal DailyChargeUsd,
    DateTime DailyWindowStartUtc,
    DateTime DailyWindowEndUtc,
    decimal? BillingPeriodLimitUsd,
    decimal BillingPeriodChargeUsd,
    DateTime? BillingPeriodStartUtc,
    DateTime? BillingPeriodEndUtc
);

public interface IPublishedGuideCostLimitService
{
    /// <summary>
    /// Evaluates cost limits for the published guide that owns <paramref name="notebookId"/> (1:1 relationship),
    /// and updates the guide's auto-disabled status if needed.
    /// </summary>
    Task<PublishedGuideCostLimitResult> EnsureWithinLimitsAsync(Guid notebookId, CancellationToken ct = default);
}

