using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.PublishedGuides;
using GuideAntsApi.Tests.BackgroundJobs;

namespace GuideAntsApi.Tests.Services.PublishedGuides;

[TestClass]
public sealed class PublishedGuideCostLimitServiceTests
{
    [TestMethod]
    public async Task EnsureWithinLimitsAsync_Blocks_when_monthly_utc_limit_is_exceeded()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"published-cost-monthly-{Guid.NewGuid():N}");
        var notebookId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = new ApplicationDbContext(options))
        {
            var projectId = Guid.NewGuid();
            seed.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = "project", Created = now });
            seed.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "Notebook", Slug = "notebook", Created = now });
            seed.PublishedGuides.Add(new PublishedGuide
            {
                Id = Guid.NewGuid(),
                GuideId = Guid.NewGuid(),
                NotebookId = notebookId,
                Active = true,
                BillingPeriodChargeLimitUsd = 1.00m
            });
            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    NotebookId = notebookId,
                    ProjectId = projectId,
                    Created = monthStart.AddDays(1),
                    ChargeUsd = 0.60m
                },
                new UsageEvent
                {
                    NotebookId = notebookId,
                    ProjectId = projectId,
                    Created = monthStart.AddDays(2),
                    ChargeUsd = 0.50m
                },
                new UsageEvent
                {
                    NotebookId = notebookId,
                    ProjectId = projectId,
                    Created = monthStart.AddDays(-1),
                    ChargeUsd = 9.99m
                });
            await seed.SaveChangesAsync();
        }

        await using var db = new ApplicationDbContext(options);
        var service = new PublishedGuideCostLimitService(db);

        var result = await service.EnsureWithinLimitsAsync(notebookId);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("monthly_limit_exceeded");
        result.BillingPeriodLimitUsd.Should().Be(1.00m);
        result.BillingPeriodChargeUsd.Should().Be(1.10m);
        result.BillingPeriodStartUtc.Should().Be(monthStart);
        result.BillingPeriodEndUtc.Should().Be(monthStart.AddMonths(1));
    }

    [TestMethod]
    public async Task EnsureWithinLimitsAsync_Prioritizes_daily_limit_when_both_daily_and_monthly_are_exceeded()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"published-cost-daily-{Guid.NewGuid():N}");
        var notebookId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = new ApplicationDbContext(options))
        {
            var projectId = Guid.NewGuid();
            seed.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = "project", Created = now });
            seed.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "Notebook", Slug = "notebook", Created = now });
            seed.PublishedGuides.Add(new PublishedGuide
            {
                Id = Guid.NewGuid(),
                GuideId = Guid.NewGuid(),
                NotebookId = notebookId,
                Active = true,
                DailyChargeLimitUsd = 0.50m,
                BillingPeriodChargeLimitUsd = 1.00m
            });
            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    NotebookId = notebookId,
                    ProjectId = projectId,
                    Created = dayStart.AddHours(1),
                    ChargeUsd = 0.40m
                },
                new UsageEvent
                {
                    NotebookId = notebookId,
                    ProjectId = projectId,
                    Created = dayStart.AddHours(2),
                    ChargeUsd = 0.30m
                },
                new UsageEvent
                {
                    NotebookId = notebookId,
                    ProjectId = projectId,
                    Created = dayStart.AddDays(-2),
                    ChargeUsd = 0.70m
                });
            await seed.SaveChangesAsync();
        }

        await using var db = new ApplicationDbContext(options);
        var service = new PublishedGuideCostLimitService(db);

        var result = await service.EnsureWithinLimitsAsync(notebookId);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("daily_limit_exceeded");
        result.DailyChargeUsd.Should().Be(0.70m);
        result.BillingPeriodChargeUsd.Should().Be(1.40m);
    }
}
