using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class UsageQueryServiceTests
{
    [TestMethod]
    public async Task GetOwnerUsageByProjectAsync_Aggregates_usage_events()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-by-project-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Usage Project",
                Slug = "usage-project",
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                Category = UsageCategory.ChatCompletion,
                Created = DateTime.UtcNow.AddHours(-1),
                ChargeUsd = 1.25m
            });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp => sp.GetRequiredService<TestDbContextFactory>());
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();
        var service = new UsageQueryService(provider.GetRequiredService<IServiceScopeFactory>());

        var summaries = await service.GetOwnerUsageByProjectAsync(from, to);

        summaries.Should().ContainSingle();
        summaries[0].ProjectId.Should().Be(projectId);
        summaries[0].TotalCostUsd.Should().Be(1.25m);
    }

    [TestMethod]
    public async Task GetOwnerUsageDetailsAsync_Filters_and_pages_results()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-details-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    Category = UsageCategory.ChatCompletion,
                    Service = "AzureOpenAI",
                    Operation = "chat",
                    Created = DateTime.UtcNow.AddHours(-2),
                    ChargeUsd = 1m
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    Category = UsageCategory.Search,
                    Service = "HybridSearch",
                    Operation = "SearchProject",
                    Created = DateTime.UtcNow.AddHours(-1),
                    ChargeUsd = 0.5m
                });
            await seed.SaveChangesAsync();
        }

        var service = CreateService(options);
        var page = await service.GetOwnerUsageDetailsAsync(new UsageDetailsQueryDto
        {
            From = from,
            To = to,
            ProjectId = projectId,
            Category = nameof(UsageCategory.ChatCompletion),
            Page = 1,
            PageSize = 10
        });

        page.Items.Should().ContainSingle();
        page.Items[0].Category.Should().Be(nameof(UsageCategory.ChatCompletion));
        page.Total.Should().Be(1);
    }

    [TestMethod]
    public async Task GetOwnerUsageBreakdownAsync_Aggregates_by_service_operation_and_category()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-breakdown-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;
        var categoryId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Breakdown Project",
                Slug = "breakdown",
                Created = DateTime.UtcNow
            });
            seed.UsageReportCategories.Add(new UsageReportCategory
            {
                Id = categoryId,
                Key = "chat",
                Title = "Chat",
                Description = "Chat usage"
            });
            seed.UsageReportCategoryOperations.Add(new UsageReportCategoryOperation
            {
                UsageReportCategoryId = categoryId,
                Operation = "chat"
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                Category = UsageCategory.ChatCompletion,
                Service = "AzureOpenAI",
                Operation = "chat",
                Created = DateTime.UtcNow.AddHours(-1),
                ChargeUsd = 2m
            });
            await seed.SaveChangesAsync();
        }

        var service = CreateService(options);
        var breakdown = await service.GetOwnerUsageBreakdownAsync(from, to, projectId);

        breakdown.ByService.Should().ContainSingle(s => s.Service == "AzureOpenAI" && s.TotalCostUsd == 2m);
        breakdown.ByOperation.Should().ContainSingle(o => o.Operation == "chat" && o.TotalCostUsd == 2m);
        breakdown.ByCategory.Should().Contain(c => c.Key == "chat" && c.TotalCostUsd == 2m);
    }

    private static UsageQueryService CreateService(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp => sp.GetRequiredService<TestDbContextFactory>());
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();
        return new UsageQueryService(provider.GetRequiredService<IServiceScopeFactory>());
    }
}
