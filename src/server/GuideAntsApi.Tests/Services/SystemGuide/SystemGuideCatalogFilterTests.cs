using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.SystemGuide;

[TestClass]
public sealed class SystemGuideCatalogFilterTests
{
    [TestMethod]
    public async Task GetHiddenGuideIdsAsync_Returns_user_and_admin_guide_ids_from_settings()
    {
        var userGuideId = Guid.NewGuid();
        var adminGuideId = Guid.NewGuid();
        var store = new Mock<IGuideAntsSystemSettingsStore>();
        store.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuideAntsSystemSettings
            {
                UserGuideId = userGuideId,
                AdminGuideId = adminGuideId
            });

        await using var context = CreateContext();
        var filter = new SystemGuideCatalogFilter(store.Object, context);
        var hidden = await filter.GetHiddenGuideIdsAsync();

        hidden.Should().BeEquivalentTo([userGuideId, adminGuideId]);
    }

    [TestMethod]
    public async Task GetHiddenGuideIdsAsync_Returns_empty_when_settings_missing()
    {
        var store = new Mock<IGuideAntsSystemSettingsStore>();
        store.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GuideAntsSystemSettings?)null);

        await using var context = CreateContext();
        var filter = new SystemGuideCatalogFilter(store.Object, context);
        var hidden = await filter.GetHiddenGuideIdsAsync();

        hidden.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetHiddenGuideIdsForCatalogAsync_Hides_system_guides_for_regular_project()
    {
        var systemGuideId = Guid.NewGuid();
        var store = new Mock<IGuideAntsSystemSettingsStore>();
        store.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuideAntsSystemSettings { UserGuideId = systemGuideId });

        await using var context = CreateContext();
        var regularProjectId = Guid.NewGuid();
        context.Projects.Add(new Project
        {
            Id = regularProjectId,
            Title = "Team Project",
            IsSystemProject = false
        });
        await context.SaveChangesAsync();

        var filter = new SystemGuideCatalogFilter(store.Object, context);
        var hidden = await filter.GetHiddenGuideIdsForCatalogAsync(regularProjectId);

        hidden.Should().Contain(systemGuideId);
    }

    [TestMethod]
    public async Task GetHiddenGuideIdsForCatalogAsync_Does_not_hide_system_guides_for_system_project()
    {
        var systemGuideId = Guid.NewGuid();
        var store = new Mock<IGuideAntsSystemSettingsStore>();
        store.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuideAntsSystemSettings { UserGuideId = systemGuideId });

        await using var context = CreateContext();
        var systemProjectId = Guid.NewGuid();
        context.Projects.Add(new Project
        {
            Id = systemProjectId,
            Title = "GuideAnts System",
            IsSystemProject = true
        });
        await context.SaveChangesAsync();

        var filter = new SystemGuideCatalogFilter(store.Object, context);
        var hidden = await filter.GetHiddenGuideIdsForCatalogAsync(systemProjectId);

        hidden.Should().BeEmpty();
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"catalog-filter-{Guid.NewGuid():N}");
        return new ApplicationDbContext(options);
    }
}
