using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideUsageServiceTests
{
    [TestMethod]
    public async Task GetGuideUsageSummaryAsync_Returns_null_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-usage-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var summary = await service.GetGuideUsageSummaryAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow.AddDays(-7),
            DateTime.UtcNow);

        summary.Should().BeNull();
    }

    [TestMethod]
    public async Task GetGuideUsageSummaryAsync_Returns_summary_for_existing_guide()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-usage-summary-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                AssistantId = guideId,
                Category = UsageCategory.ChatCompletion,
                Created = DateTime.UtcNow.AddHours(-2),
                ChargeUsd = 0.5m
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var summary = await service.GetGuideUsageSummaryAsync(projectId, guideId, from, to);

        summary.Should().NotBeNull();
        summary!.GuideId.Should().Be(guideId);
    }
}
