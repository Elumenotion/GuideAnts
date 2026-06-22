using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services;

public interface IUsageQueryService
{
    Task<UsageSummaryDto> GetOwnerUsageSummaryAsync(DateTime from, DateTime to, UsageBucketDto bucket);
    Task<IReadOnlyList<ProjectUsageSummaryDto>> GetOwnerUsageByProjectAsync(DateTime from, DateTime to);
    Task<UsageSummaryDto?> GetProjectUsageSummaryAsync(Guid projectId, DateTime from, DateTime to, UsageBucketDto bucket);
    Task<PagedResultDto<UsageEventDto>> GetOwnerUsageDetailsAsync(UsageDetailsQueryDto query);
    Task<PagedResultDto<UsageEventDto>?> GetProjectUsageDetailsAsync(Guid projectId, UsageDetailsQueryDto query);
    Task<UsageBreakdownWithCategoriesDto> GetOwnerUsageBreakdownAsync(DateTime from, DateTime to, Guid? projectId = null);
}

public sealed class UsageQueryService(IServiceScopeFactory scopeFactory) : IUsageQueryService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    

    private static DateTime NormalizeBucketStart(DateTime from, UsageBucketDto bucket)
    {
        var utc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        return bucket switch
        {
            UsageBucketDto.Day => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
            UsageBucketDto.Week => new DateTime(utc.AddDays(-(int)utc.DayOfWeek).Year, utc.AddDays(-(int)utc.DayOfWeek).Month, utc.AddDays(-(int)utc.DayOfWeek).Day, 0, 0, 0, DateTimeKind.Utc),
            UsageBucketDto.Month => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => utc
        };
    }

    private static DateTime AddBuckets(DateTime anchor, UsageBucketDto bucket, int index)
    {
        return bucket switch
        {
            UsageBucketDto.Day => anchor.AddDays(index),
            UsageBucketDto.Week => anchor.AddDays(index * 7),
            UsageBucketDto.Month => anchor.AddMonths(index),
            _ => anchor
        };
    }

    public async Task<UsageSummaryDto> GetOwnerUsageSummaryAsync(DateTime from, DateTime to, UsageBucketDto bucket)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // normalize to UTC bounds inclusive of the end date
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        var baseQuery = db.UsageEvents
            .AsNoTracking()
            .Where(e => e.Created >= fromUtc && e.Created <= toUtc);

        // baseQuery = RestrictToOwnerProjects(baseQuery, db, user.Id);

        var totalEvents = await baseQuery.LongCountAsync();
        var totalCost = await baseQuery.SumAsync(e => (decimal?)e.ChargeUsd) ?? 0m;

        var anchor = NormalizeBucketStart(from, bucket);
        var bucketedRaw = await baseQuery
            .Select(e => new
            {
                Index = bucket == UsageBucketDto.Day
                    ? EF.Functions.DateDiffDay(anchor, e.Created)
                    : bucket == UsageBucketDto.Week
                        ? EF.Functions.DateDiffWeek(anchor, e.Created)
                        : EF.Functions.DateDiffMonth(anchor, e.Created),
                Cost = e.ChargeUsd ?? 0m
            })
            .GroupBy(x => x.Index)
            .Select(g => new { Index = g.Key, TotalEvents = g.LongCount(), TotalCost = g.Sum(x => x.Cost) })
            .ToListAsync();

        var timeSeries = bucketedRaw
            .Select(b => new UsageTimePointDto(AddBuckets(anchor, bucket, b.Index), b.TotalEvents, b.TotalCost))
            .OrderBy(tp => tp.PeriodStart)
            .ToList();

        var byCategory = await baseQuery
            .GroupBy(e => e.Category)
            .Select(g => new UsageCategoryTotalsDto(g.Key.ToString(), g.LongCount(), g.Sum(e => e.ChargeUsd ?? 0m)))
            .ToListAsync();

        return new UsageSummaryDto(from, to, bucket, new UsageTotals(totalEvents, totalCost), timeSeries, byCategory);
    }

    public async Task<IReadOnlyList<ProjectUsageSummaryDto>> GetOwnerUsageByProjectAsync(DateTime from, DateTime to)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ownerProjectIds = db.Projects.Where(p => !p.IsSystemProject).Select(p => p.Id);

        var q = db.UsageEvents.AsNoTracking()
            .Where(e => e.Created >= from && e.Created <= to && e.ProjectId != null && ownerProjectIds.Contains(e.ProjectId.Value));

        var grouped = await q
            .GroupBy(e => e.ProjectId)
            .Select(g => new
            {
                ProjectId = g.Key!.Value,
                TotalEvents = g.LongCount(),
                TotalCost = g.Sum(e => e.ChargeUsd ?? 0m)
            })
            .ToListAsync();

        var ids = grouped.Select(x => x.ProjectId).ToList();
        var titles = await db.Projects.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Title })
            .ToListAsync();

        var result = grouped
            .Join(titles, g => g.ProjectId, t => t.Id, (g, t) => new ProjectUsageSummaryDto(t.Id, t.Title, g.TotalEvents, g.TotalCost))
            .OrderByDescending(x => x.TotalCostUsd)
            .ToList();

        return result;
    }

    public async Task<UsageSummaryDto?> GetProjectUsageSummaryAsync(Guid projectId, DateTime from, DateTime to, UsageBucketDto bucket)
    {

using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        var baseQuery = db.UsageEvents.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Created >= fromUtc && e.Created <= toUtc);

        var totalEvents = await baseQuery.LongCountAsync();
        var totalCost = await baseQuery.SumAsync(e => (decimal?)e.ChargeUsd) ?? 0m;

        var anchor = NormalizeBucketStart(from, bucket);
        var bucketedRaw = await baseQuery
            .Select(e => new
            {
                Index = bucket == UsageBucketDto.Day
                    ? EF.Functions.DateDiffDay(anchor, e.Created)
                    : bucket == UsageBucketDto.Week
                        ? EF.Functions.DateDiffWeek(anchor, e.Created)
                        : EF.Functions.DateDiffMonth(anchor, e.Created),
                Cost = e.ChargeUsd ?? 0m
            })
            .GroupBy(x => x.Index)
            .Select(g => new { Index = g.Key, TotalEvents = g.LongCount(), TotalCost = g.Sum(x => x.Cost) })
            .ToListAsync();

        var timeSeries = bucketedRaw
            .Select(b => new UsageTimePointDto(AddBuckets(anchor, bucket, b.Index), b.TotalEvents, b.TotalCost))
            .OrderBy(tp => tp.PeriodStart)
            .ToList();

        var byCategory = await baseQuery
            .GroupBy(e => e.Category)
            .Select(g => new UsageCategoryTotalsDto(g.Key.ToString(), g.LongCount(), g.Sum(e => e.ChargeUsd ?? 0m)))
            .ToListAsync();

        return new UsageSummaryDto(from, to, bucket, new UsageTotals(totalEvents, totalCost), timeSeries, byCategory);
    }

    public async Task<PagedResultDto<UsageEventDto>> GetOwnerUsageDetailsAsync(UsageDetailsQueryDto query)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fromUtc = DateTime.SpecifyKind(query.From, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(query.To, DateTimeKind.Utc);
        var q = db.UsageEvents.AsNoTracking().Where(e => e.Created >= fromUtc && e.Created <= toUtc);
        // q = RestrictToOwnerProjects(q, db, user.Id);

        if (query.ProjectId.HasValue) q = q.Where(e => e.ProjectId == query.ProjectId);
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            if (Enum.TryParse<UsageCategory>(query.Category, out var cat))
                q = q.Where(e => e.Category == cat);
        }
        if (!string.IsNullOrWhiteSpace(query.Service)) q = q.Where(e => e.Service == query.Service);
        if (!string.IsNullOrWhiteSpace(query.Operation)) q = q.Where(e => e.Operation == query.Operation);

        var total = await q.LongCountAsync();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var evts = await q.OrderByDescending(e => e.Created)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new {
                e.Id, e.Created, e.ProjectId, e.NotebookId, e.ConversationId, e.ContentFileId, e.NotebookFileId,
                e.Category, e.Service, e.Operation, e.ModelDeploymentId,
                e.ValueInput, e.ValueCachedInput, e.ValueReasoning, e.ValueOutput, e.ValueOther,
                e.MetadataJson, e.ChargeUsd
            })
            .ToListAsync();

        var items = evts.Select(e => new UsageEventDto(
            e.Id, e.Created, e.ProjectId, e.NotebookId, e.ConversationId, e.ContentFileId, e.NotebookFileId,
            e.Category.ToString(), e.Service, e.Operation, e.ModelDeploymentId,
            e.ValueInput, e.ValueCachedInput, e.ValueReasoning, e.ValueOutput, e.ValueOther,
            e.MetadataJson, e.ChargeUsd)).ToList();

        return new PagedResultDto<UsageEventDto>(items, total, page, pageSize);
    }

    public async Task<PagedResultDto<UsageEventDto>?> GetProjectUsageDetailsAsync(Guid projectId, UsageDetailsQueryDto query)
    {

using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fromUtc = DateTime.SpecifyKind(query.From, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(query.To, DateTimeKind.Utc);
        var q = db.UsageEvents.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Created >= fromUtc && e.Created <= toUtc);

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            if (Enum.TryParse<UsageCategory>(query.Category, out var cat))
                q = q.Where(e => e.Category == cat);
        }
        if (!string.IsNullOrWhiteSpace(query.Service)) q = q.Where(e => e.Service == query.Service);
        if (!string.IsNullOrWhiteSpace(query.Operation)) q = q.Where(e => e.Operation == query.Operation);

        var total = await q.LongCountAsync();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var evts = await q.OrderByDescending(e => e.Created)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new {
                e.Id, e.Created, e.ProjectId, e.NotebookId, e.ConversationId, e.ContentFileId, e.NotebookFileId,
                e.Category, e.Service, e.Operation, e.ModelDeploymentId,
                e.ValueInput, e.ValueCachedInput, e.ValueReasoning, e.ValueOutput, e.ValueOther,
                e.MetadataJson, e.ChargeUsd
            })
            .ToListAsync();

        var items = evts.Select(e => new UsageEventDto(
            e.Id, e.Created, e.ProjectId, e.NotebookId, e.ConversationId, e.ContentFileId, e.NotebookFileId,
            e.Category.ToString(), e.Service, e.Operation, e.ModelDeploymentId,
            e.ValueInput, e.ValueCachedInput, e.ValueReasoning, e.ValueOutput, e.ValueOther,
            e.MetadataJson, e.ChargeUsd)).ToList();

        return new PagedResultDto<UsageEventDto>(items, total, page, pageSize);
    }

    public async Task<UsageBreakdownWithCategoriesDto> GetOwnerUsageBreakdownAsync(DateTime from, DateTime to, Guid? projectId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        
        // Step 1: Project IDs this user owns
        var ownerProjectIds = await db.Projects
            .Where(p => !p.IsSystemProject)
            .Select(p => p.Id)
            .ToListAsync();

        // Step 2: Build a simple filtered query
        var q = db.UsageEvents.AsNoTracking()
            .Where(e => e.Created >= fromUtc && e.Created <= toUtc)
            .Where(e => e.ProjectId != null && ownerProjectIds.Contains(e.ProjectId.Value));
        
        if (projectId.HasValue)
        {
            q = q.Where(e => e.ProjectId == projectId.Value);
        }

        // Step 3: Get service breakdown
        var byService = await q
            .GroupBy(e => e.Service)
            .Select(g => new ServiceCostDto(g.Key, g.LongCount(), g.Sum(e => (decimal?)e.ChargeUsd) ?? 0m))
            .ToListAsync();
        byService = byService.OrderByDescending(x => x.TotalCostUsd).ToList();

        // Step 4: Get operation breakdown
        var byOperation = await q
            .GroupBy(e => new { e.Service, e.Operation })
            .Select(g => new OperationCostDto(g.Key.Service, g.Key.Operation, g.LongCount(), g.Sum(e => (decimal?)e.ChargeUsd) ?? 0m))
            .ToListAsync();
        byOperation = byOperation.OrderByDescending(x => x.TotalCostUsd).ToList();

        // Step 5: Load categories with their operation mappings
        var categories = await db.UsageReportCategories
            .Include(c => c.Operations)
            .ToListAsync();

        // Build operation -> category lookup (each operation string maps to one category)
        var operationToCategory = new Dictionary<string, UsageReportCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in categories)
        {
            foreach (var op in cat.Operations)
            {
                operationToCategory.TryAdd(op.Operation, cat);
            }
        }

        // Step 6: Get usage grouped by operation
        var usageByOperation = await q
            .GroupBy(e => e.Operation)
            .Select(g => new { Operation = g.Key, Count = g.LongCount(), Cost = g.Sum(e => (decimal?)e.ChargeUsd) ?? 0m })
            .ToListAsync();

        // Step 7: Aggregate by category
        var categoryTotals = new Dictionary<Guid, (UsageReportCategory Cat, long Count, decimal Cost)>();
        long otherCount = 0;
        decimal otherCost = 0m;

        foreach (var op in usageByOperation)
        {
            if (operationToCategory.TryGetValue(op.Operation, out var cat))
            {
                if (categoryTotals.TryGetValue(cat.Id, out var existing))
                {
                    categoryTotals[cat.Id] = (existing.Cat, existing.Count + op.Count, existing.Cost + op.Cost);
                }
                else
                {
                    categoryTotals[cat.Id] = (cat, op.Count, op.Cost);
                }
            }
            else
            {
                otherCount += op.Count;
                otherCost += op.Cost;
            }
        }

        var byCategory = categoryTotals.Values
            .Select(t => new ReportCategoryDto(t.Cat.Id, t.Cat.Key, t.Cat.Title, t.Cat.Description, t.Count, t.Cost))
            .ToList();

        if (otherCount > 0)
        {
            byCategory.Add(new ReportCategoryDto(Guid.Empty, "Other", "Other", "Uncategorized usage", otherCount, otherCost));
        }

        byCategory = byCategory.OrderByDescending(x => x.TotalCostUsd).ToList();

        return new UsageBreakdownWithCategoriesDto(byService, byOperation, byCategory);
    }
}


