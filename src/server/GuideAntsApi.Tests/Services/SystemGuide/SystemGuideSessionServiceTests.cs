using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.SystemGuide;

[TestClass]
public sealed class SystemGuideSessionServiceTests
{
    [TestMethod]
    public async Task GetSessionAsync_admin_returns_admin_published_guide_config()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        var service = CreateSessionService(db, fixture.Settings);

        var session = await service.GetSessionAsync(CreateUser(Role.Admin));

        session.Should().NotBeNull();
        session!.PublishedGuideId.Should().Be(fixture.Settings.AdminPublishedGuideId!.Value);
        session.GuideId.Should().Be(fixture.Settings.AdminGuideId!.Value);
        session.NotebookId.Should().Be(fixture.Settings.AdminNotebookId!.Value);
        session.ProjectId.Should().Be(fixture.Settings.ProjectId!.Value);
        session.IsAdminGuide.Should().BeTrue();
        session.GuideName.Should().Be(GuideAntsSystemSeeder.AdminGuideName);
        session.ClientBridgeId.Should().Be(GuideAntsSystemSettings.DefaultClientBridgeId);
        session.CommandMode.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetSessionAsync_contributor_returns_user_published_guide_config()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        var service = CreateSessionService(db, fixture.Settings);

        var session = await service.GetSessionAsync(CreateUser(Role.Contributor));

        session.Should().NotBeNull();
        session!.PublishedGuideId.Should().Be(fixture.Settings.UserPublishedGuideId!.Value);
        session.GuideId.Should().Be(fixture.Settings.UserGuideId!.Value);
        session.IsAdminGuide.Should().BeFalse();
        session.GuideName.Should().Be(GuideAntsSystemSeeder.UserGuideName);
    }

    [TestMethod]
    public async Task GetSessionAsync_reader_returns_user_published_guide_config()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        var service = CreateSessionService(db, fixture.Settings);

        var session = await service.GetSessionAsync(CreateUser(Role.Reader));

        session.Should().NotBeNull();
        session!.PublishedGuideId.Should().Be(fixture.Settings.UserPublishedGuideId!.Value);
        session.IsAdminGuide.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetSessionAsync_pending_returns_null()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        var service = CreateSessionService(db, fixture.Settings);

        var session = await service.GetSessionAsync(CreateUser(Role.Pending));

        session.Should().BeNull();
    }

    [TestMethod]
    public async Task GetWorkspaceAsync_admin_returns_project_slug()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        var service = CreateSessionService(db, fixture.Settings);

        var workspace = await service.GetWorkspaceAsync(CreateUser(Role.Admin));

        workspace.Should().NotBeNull();
        workspace!.ProjectId.Should().Be(fixture.Project.Id);
        workspace.ProjectSlug.Should().Be(GuideAntsSystemSeeder.SystemProjectSlug);
    }

    [TestMethod]
    public async Task GetWorkspaceAsync_contributor_returns_null()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        var service = CreateSessionService(db, fixture.Settings);

        var workspace = await service.GetWorkspaceAsync(CreateUser(Role.Contributor));

        workspace.Should().BeNull();
    }

    [TestMethod]
    public async Task GetProjectsAsync_excludes_system_project()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedSystemGuideFixtureAsync(db);
        db.Projects.Add(new Project
        {
            Title = "Visible Project",
            Slug = "visible-project",
            Description = string.Empty
        });
        await db.SaveChangesAsync();

        var service = CreateProjectService(db);
        var projects = (await service.GetProjectsAsync()).ToList();

        projects.Should().ContainSingle(p => p.Title == "Visible Project");
        projects.Should().NotContain(p => p.Id == fixture.Project.Id);
    }

    private static ProjectService CreateProjectService(ApplicationDbContext db)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(db);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = Path.GetTempPath() })
            .Build();

        return new ProjectService(scopeFactoryMock.Object, config, NullLogger<ProjectService>.Instance);
    }

    private static SystemGuideSessionService CreateSessionService(
        ApplicationDbContext db,
        GuideAntsSystemSettings settings)
    {
        var store = new Mock<IGuideAntsSystemSettingsStore>();
        store.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return new SystemGuideSessionService(db, store.Object);
    }

    private static CurrentUserContext CreateUser(Role role) =>
        new(
            Guid.NewGuid(),
            "Test User",
            "test@guideants.local",
            role,
            false,
            Guid.NewGuid(),
            DateTime.UtcNow);

    private static ApplicationDbContext CreateDbContext()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"gg-session-{Guid.NewGuid():N}");
        return new ApplicationDbContext(options);
    }

    private static async Task<SystemGuideFixture> SeedSystemGuideFixtureAsync(ApplicationDbContext db)
    {
        var project = new Project
        {
            Title = GuideAntsSystemSeeder.SystemProjectTitle,
            Slug = GuideAntsSystemSeeder.SystemProjectSlug,
            Description = string.Empty,
            IsSystemProject = true
        };
        db.Projects.Add(project);

        var userGuide = new Assistant
        {
            Name = GuideAntsSystemSeeder.UserGuideName,
            Kind = AssistantKind.Guide,
            IsGlobal = true,
            IsActive = true
        };
        var adminGuide = new Assistant
        {
            Name = GuideAntsSystemSeeder.AdminGuideName,
            Kind = AssistantKind.Guide,
            IsGlobal = true,
            IsActive = true
        };
        db.Assistants.AddRange(userGuide, adminGuide);
        await db.SaveChangesAsync();

        var userNotebook = new Notebook
        {
            Title = "User Notebook",
            Slug = "user-notebook",
            ProjectId = project.Id,
            GuideId = userGuide.Id
        };
        var adminNotebook = new Notebook
        {
            Title = "Admin Notebook",
            Slug = "admin-notebook",
            ProjectId = project.Id,
            GuideId = adminGuide.Id
        };
        db.Notebooks.AddRange(userNotebook, adminNotebook);
        await db.SaveChangesAsync();

        var userPublished = new PublishedGuide
        {
            GuideId = userGuide.Id,
            NotebookId = userNotebook.Id,
            Active = true,
            CommandMode = true,
            AuthMode = PublishedGuideAuthMode.AppIdentity
        };
        var adminPublished = new PublishedGuide
        {
            GuideId = adminGuide.Id,
            NotebookId = adminNotebook.Id,
            Active = true,
            CommandMode = true,
            AuthMode = PublishedGuideAuthMode.AppIdentity
        };
        db.PublishedGuides.AddRange(userPublished, adminPublished);
        await db.SaveChangesAsync();

        var settings = new GuideAntsSystemSettings
        {
            ProjectId = project.Id,
            UserGuideId = userGuide.Id,
            AdminGuideId = adminGuide.Id,
            UserNotebookId = userNotebook.Id,
            AdminNotebookId = adminNotebook.Id,
            UserPublishedGuideId = userPublished.Id,
            AdminPublishedGuideId = adminPublished.Id,
            ClientBridgeId = GuideAntsSystemSettings.DefaultClientBridgeId
        };

        return new SystemGuideFixture(project, settings);
    }

    private sealed record SystemGuideFixture(Project Project, GuideAntsSystemSettings Settings);
}
