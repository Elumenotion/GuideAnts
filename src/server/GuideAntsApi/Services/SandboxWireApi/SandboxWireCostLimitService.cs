using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.PublishedGuides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SandboxWireApi;

public sealed record SandboxWireCostLimitScope(
    Guid OwnerAssistantId,
    decimal? DailyLimitUsd,
    decimal? MonthlyLimitUsd);

public interface ISandboxWireCostLimitService
{
    Task<PublishedGuideCostLimitResult> EnsureWithinLimitsAsync(
        SandboxWireCostLimitScope scope,
        Guid notebookId,
        CancellationToken ct = default);
}

public sealed class SandboxWireCostLimitService : ISandboxWireCostLimitService
{
    private readonly ApplicationDbContext _db;

    public SandboxWireCostLimitService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<PublishedGuideCostLimitResult> EnsureWithinLimitsAsync(
        SandboxWireCostLimitScope scope,
        Guid notebookId,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var dayStart = StartOfUtcDay(nowUtc);
        var dayEnd = dayStart.AddDays(1);
        var monthStart = StartOfUtcMonth(nowUtc);
        var monthEnd = monthStart.AddMonths(1);

        if (scope.DailyLimitUsd == null && scope.MonthlyLimitUsd == null)
        {
            return new PublishedGuideCostLimitResult(
                Allowed: true,
                Reason: null,
                DailyLimitUsd: null,
                DailyChargeUsd: 0m,
                DailyWindowStartUtc: dayStart,
                DailyWindowEndUtc: dayEnd,
                BillingPeriodLimitUsd: null,
                BillingPeriodChargeUsd: 0m,
                BillingPeriodStartUtc: monthStart,
                BillingPeriodEndUtc: monthEnd);
        }

        decimal dailyCharge = 0m;
        if (scope.DailyLimitUsd.HasValue)
        {
            dailyCharge = await SumSandboxWireChargesAsync(
                notebookId,
                dayStart,
                dayEnd,
                ct);
        }

        decimal monthlyCharge = 0m;
        if (scope.MonthlyLimitUsd.HasValue)
        {
            monthlyCharge = await SumSandboxWireChargesAsync(
                notebookId,
                monthStart,
                monthEnd,
                ct);
        }

        string? reason = null;
        if (scope.DailyLimitUsd.HasValue && dailyCharge >= scope.DailyLimitUsd.Value)
        {
            reason = $"Daily sandbox wire API charge limit of ${scope.DailyLimitUsd.Value:F2} exceeded.";
        }
        else if (scope.MonthlyLimitUsd.HasValue && monthlyCharge >= scope.MonthlyLimitUsd.Value)
        {
            reason = $"Monthly sandbox wire API charge limit of ${scope.MonthlyLimitUsd.Value:F2} exceeded.";
        }

        return new PublishedGuideCostLimitResult(
            Allowed: reason == null,
            Reason: reason,
            DailyLimitUsd: scope.DailyLimitUsd,
            DailyChargeUsd: dailyCharge,
            DailyWindowStartUtc: dayStart,
            DailyWindowEndUtc: dayEnd,
            BillingPeriodLimitUsd: scope.MonthlyLimitUsd,
            BillingPeriodChargeUsd: monthlyCharge,
            BillingPeriodStartUtc: monthStart,
            BillingPeriodEndUtc: monthEnd);
    }

    private async Task<decimal> SumSandboxWireChargesAsync(
        Guid notebookId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken ct)
    {
        // Key the sum on notebook + source channel only. Sandbox wire usage is tagged with
        // SourceChannelValue for the duration of a sandbox request; chat runs the *target*
        // assistant while media runs are attributed to the *owner*, so filtering on any single
        // AssistantId would silently drop one of them. A notebook maps to one guide, so
        // notebook + source channel captures the complete sandbox wire spend for this owner.
        return await _db.UsageEvents
            .AsNoTracking()
            .Where(e =>
                e.NotebookId == notebookId
                && e.SourceChannel == SandboxWireExecutionContext.SourceChannelValue
                && e.Created >= windowStartUtc
                && e.Created < windowEndUtc)
            .SumAsync(e => (decimal?)e.ChargeUsd, ct) ?? 0m;
    }

    private static DateTime StartOfUtcDay(DateTime utcNow) =>
        new(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime StartOfUtcMonth(DateTime utcNow) =>
        new(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
