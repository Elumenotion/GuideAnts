using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.SystemGuide;

[TestClass]
public sealed class SystemProjectAccessGuardTests
{
    [TestMethod]
    public async Task EnsureReadAccessAsync_returns_null_for_non_system_project()
    {
        await using var db = CreateDbContext();
        var project = await SeedRegularProjectAsync(db);
        var guard = CreateGuard(db, Role.Reader);

        var result = await guard.EnsureReadAccessAsync(project.Id);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task EnsureReadAccessAsync_returns_not_found_for_reader_on_system_project()
    {
        await using var db = CreateDbContext();
        var systemProject = await SeedSystemProjectAsync(db);
        var guard = CreateGuard(db, Role.Reader);

        var result = await guard.EnsureReadAccessAsync(systemProject.Id);

        result.Should().NotBeNull();
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }

    [TestMethod]
    public async Task EnsureReadAccessAsync_returns_null_for_admin_on_system_project()
    {
        await using var db = CreateDbContext();
        var systemProject = await SeedSystemProjectAsync(db);
        var guard = CreateGuard(db, Role.Admin);

        var result = await guard.EnsureReadAccessAsync(systemProject.Id);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task EnsureDeleteAllowedAsync_blocks_system_project_delete()
    {
        await using var db = CreateDbContext();
        var systemProject = await SeedSystemProjectAsync(db);
        var guard = CreateGuard(db, Role.Admin);

        var result = await guard.EnsureDeleteAllowedAsync(systemProject.Id);

        result.Should().NotBeNull();
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>();
    }

    [TestMethod]
    public async Task EnsureDeleteAllowedAsync_allows_regular_project_delete()
    {
        await using var db = CreateDbContext();
        var project = await SeedRegularProjectAsync(db);
        var guard = CreateGuard(db, Role.Admin);

        var result = await guard.EnsureDeleteAllowedAsync(project.Id);

        result.Should().BeNull();
    }

    private static SystemProjectAccessGuard CreateGuard(ApplicationDbContext db, Role role)
    {
        var userId = Guid.NewGuid();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser
            .Setup(x => x.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext(
                userId,
                "Test User",
                "test@guideants.local",
                role,
                false,
                Guid.NewGuid(),
                DateTime.UtcNow));

        return new SystemProjectAccessGuard(db, currentUser.Object);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"gg-guard-{Guid.NewGuid():N}");
        return new ApplicationDbContext(options);
    }

    private static async Task<Project> SeedRegularProjectAsync(ApplicationDbContext db)
    {
        var project = new Project
        {
            Title = "Regular Project",
            Slug = "regular-project",
            Description = string.Empty
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<Project> SeedSystemProjectAsync(ApplicationDbContext db)
    {
        var project = new Project
        {
            Title = GuideAntsSystemSeeder.SystemProjectTitle,
            Slug = GuideAntsSystemSeeder.SystemProjectSlug,
            Description = string.Empty,
            IsSystemProject = true
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }
}
