using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.Services.PublishedGuides;

public sealed class PublishedGuideCostLimitService : IPublishedGuideCostLimitService
{
    private readonly ApplicationDbContext _db;

    public PublishedGuideCostLimitService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<PublishedGuideCostLimitResult> EnsureWithinLimitsAsync(Guid notebookId, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var dayStart = StartOfUtcDay(nowUtc);
        var dayEnd = dayStart.AddDays(1);
        var monthStart = StartOfUtcMonth(nowUtc);
        var monthEnd = monthStart.AddMonths(1);

        var pg = await _db.PublishedGuides
            .AsNoTracking()
            .Include(x => x.Notebook)
                .ThenInclude(n => n.Project)
            .FirstOrDefaultAsync(x => x.NotebookId == notebookId, ct);

        if (pg == null)
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

        var dailyLimit = pg.DailyChargeLimitUsd;
        var monthlyLimit = pg.BillingPeriodChargeLimitUsd;

        if (dailyLimit == null && monthlyLimit == null)
        {
            return new PublishedGuideCostLimitResult(
                Allowed: true,
                Reason: null,
                DailyLimitUsd: null,
                DailyChargeUsd: 0m,
                DailyWindowStartUtc: dayStart,
                DailyWindowEndUtc: dayEnd,
                BillingPeriodLimitUsd: monthlyLimit,
                BillingPeriodChargeUsd: 0m,
                BillingPeriodStartUtc: monthStart,
                BillingPeriodEndUtc: monthEnd);
        }

        decimal dailyCharge = 0m;
        if (dailyLimit.HasValue)
        {
            dailyCharge = await _db.UsageEvents
                .AsNoTracking()
                .Where(e => e.NotebookId == notebookId && e.Created >= dayStart && e.Created < dayEnd)
                .SumAsync(e => (decimal?)e.ChargeUsd, ct) ?? 0m;
        }

        decimal monthlyCharge = 0m;
        if (monthlyLimit.HasValue)
        {
            monthlyCharge = await _db.UsageEvents
                .AsNoTracking()
                .Where(e => e.NotebookId == notebookId && e.Created >= monthStart && e.Created < monthEnd)
                .SumAsync(e => (decimal?)e.ChargeUsd, ct) ?? 0m;
        }

        string? reason = null;
        var dailyExceeded = dailyLimit.HasValue && dailyCharge >= dailyLimit.Value;
        var monthlyExceeded = monthlyLimit.HasValue && monthlyCharge >= monthlyLimit.Value;
        if (dailyExceeded)
        {
            reason = "daily_limit_exceeded";
        }
        else if (monthlyExceeded)
        {
            reason = "monthly_limit_exceeded";
        }

        return new PublishedGuideCostLimitResult(
            Allowed: !dailyExceeded && !monthlyExceeded,
            Reason: reason,
            DailyLimitUsd: dailyLimit,
            DailyChargeUsd: dailyCharge,
            DailyWindowStartUtc: dayStart,
            DailyWindowEndUtc: dayEnd,
            BillingPeriodLimitUsd: monthlyLimit,
            BillingPeriodChargeUsd: monthlyCharge,
            BillingPeriodStartUtc: monthStart,
            BillingPeriodEndUtc: monthEnd);
    }

    private static DateTime StartOfUtcDay(DateTime utcNow)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime StartOfUtcMonth(DateTime utcNow)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
