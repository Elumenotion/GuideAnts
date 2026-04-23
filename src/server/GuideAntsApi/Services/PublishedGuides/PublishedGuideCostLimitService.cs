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
                DailyWindowStartUtc: StartOfUtcDay(DateTime.UtcNow),
                DailyWindowEndUtc: StartOfUtcDay(DateTime.UtcNow).AddDays(1),
                BillingPeriodLimitUsd: null,
                BillingPeriodChargeUsd: 0m,
                BillingPeriodStartUtc: null,
                BillingPeriodEndUtc: null);
        }

        var dailyLimit = pg.DailyChargeLimitUsd;

        if (dailyLimit == null)
        {
            return new PublishedGuideCostLimitResult(
                Allowed: true,
                Reason: null,
                DailyLimitUsd: null,
                DailyChargeUsd: 0m,
                DailyWindowStartUtc: StartOfUtcDay(DateTime.UtcNow),
                DailyWindowEndUtc: StartOfUtcDay(DateTime.UtcNow).AddDays(1),
                BillingPeriodLimitUsd: null,
                BillingPeriodChargeUsd: 0m,
                BillingPeriodStartUtc: null,
                BillingPeriodEndUtc: null);
        }

        var nowUtc = DateTime.UtcNow;
        var dayStart = StartOfUtcDay(nowUtc);
        var dayEnd = dayStart.AddDays(1);

        var dailyCharge = await _db.UsageEvents
            .AsNoTracking()
            .Where(e => e.NotebookId == notebookId && e.Created >= dayStart && e.Created < dayEnd)
            .SumAsync(e => (decimal?)e.ChargeUsd, ct) ?? 0m;

        string? reason = null;
        var exceeded = dailyLimit.HasValue && dailyCharge >= dailyLimit.Value;
        if (exceeded)
        {
            reason = "daily_limit_exceeded";
        }

        return new PublishedGuideCostLimitResult(
            Allowed: !exceeded,
            Reason: reason,
            DailyLimitUsd: dailyLimit,
            DailyChargeUsd: dailyCharge,
            DailyWindowStartUtc: dayStart,
            DailyWindowEndUtc: dayEnd,
            BillingPeriodLimitUsd: null,
            BillingPeriodChargeUsd: 0m,
            BillingPeriodStartUtc: null,
            BillingPeriodEndUtc: null);
    }

    private static DateTime StartOfUtcDay(DateTime utcNow)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
    }
}
