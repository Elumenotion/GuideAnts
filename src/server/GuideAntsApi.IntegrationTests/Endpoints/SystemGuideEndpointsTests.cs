using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Models.SystemGuide;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class SystemGuideEndpointsTests : BaseEndpointTest
{
    private const string SystemProjectTitle = "GuideAnts System";
    private const string SystemProjectSlug = "guideants-system";
    private const string UserGuideName = "GuideAnts Guide";
    private const string AdminGuideName = "GuideAnts Guide Admin";

    private SystemGuideFixture _fixture = null!;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        _fixture = await SeedSystemGuideFixtureAsync();
    }

    protected override async Task CleanDatabaseAsync()
    {
        if (SharedFactory != null)
        {
            using var scope = SharedFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // System-guide fixture cleanup (FK order). Without this, fixed assistant names
            // collide on IX_Assistants_Name across tests in this class.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM ApplicationSettings WHERE SectionName = '{GuideAntsSystemSettings.SectionName}';");
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM Notebooks WHERE ProjectId IN (SELECT Id FROM Projects WHERE IsSystemProject = 1);");
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM Assistants WHERE Name IN (N'GuideAnts Guide', N'GuideAnts Guide Admin');");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Projects WHERE IsSystemProject = 1;");
        }

        await base.CleanDatabaseAsync();
    }

    [TestMethod]
    public async Task GetProjects_excludes_system_project()
    {
        SetupAuthentication(Role.Admin);

        var createResponse = await Client!.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto("Visible Project", "integration test"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await Client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var projects = await response.Content.ReadFromJsonAsync<List<ProjectDto>>();
        projects.Should().NotBeNull();
        projects!.Should().Contain(p => p.Title == "Visible Project");
        projects.Should().NotContain(p => p.Id == _fixture.ProjectId);
    }

    [TestMethod]
    public async Task GetProject_system_project_as_reader_returns_not_found()
    {
        SetupAuthentication(Role.Reader);

        var response = await Client!.GetAsync($"/api/projects/{_fixture.ProjectId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task GetProject_system_project_as_admin_returns_ok()
    {
        SetupAuthentication(Role.Admin);

        var response = await Client!.GetAsync($"/api/projects/{_fixture.ProjectId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var project = await response.Content.ReadFromJsonAsync<ProjectDto>();
        project.Should().NotBeNull();
        project!.Id.Should().Be(_fixture.ProjectId);
    }

    [TestMethod]
    public async Task GetSystemGuideSession_admin_returns_admin_published_guide()
    {
        SetupAuthentication(Role.Admin);

        var response = await Client!.GetAsync("/api/system-guide/session");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var session = await response.Content.ReadFromJsonAsync<SystemGuideSessionDto>();
        session.Should().NotBeNull();
        session!.PublishedGuideId.Should().Be(_fixture.AdminPublishedGuideId);
        session.IsAdminGuide.Should().BeTrue();
        session.GuideName.Should().Be(AdminGuideName);
    }

    [TestMethod]
    public async Task GetSystemGuideSession_contributor_returns_user_published_guide()
    {
        SetupAuthentication(Role.Contributor);

        var response = await Client!.GetAsync("/api/system-guide/session");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var session = await response.Content.ReadFromJsonAsync<SystemGuideSessionDto>();
        session.Should().NotBeNull();
        session!.PublishedGuideId.Should().Be(_fixture.UserPublishedGuideId);
        session.IsAdminGuide.Should().BeFalse();
        session.GuideName.Should().Be(UserGuideName);
    }

    [TestMethod]
    public async Task GetSystemGuideSession_pending_returns_forbidden()
    {
        SetupAuthentication(Role.Pending);

        var response = await Client!.GetAsync("/api/system-guide/session");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task GetSystemGuideWorkspace_contributor_returns_not_found()
    {
        SetupAuthentication(Role.Contributor);

        var response = await Client!.GetAsync("/api/system-guide/workspace");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task GetSystemGuideWorkspace_admin_returns_project_slug()
    {
        SetupAuthentication(Role.Admin);

        var response = await Client!.GetAsync("/api/system-guide/workspace");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var workspace = await response.Content.ReadFromJsonAsync<SystemGuideWorkspaceDto>();
        workspace.Should().NotBeNull();
        workspace!.ProjectId.Should().Be(_fixture.ProjectId);
        workspace.ProjectSlug.Should().Be(SystemProjectSlug);
    }

    [TestMethod]
    public async Task GetSandboxAdminRequirements_notebook_without_guide_returns_bad_request()
    {
        SetupAuthentication(Role.Admin);

        Guid notebookId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var notebook = new Notebook
            {
                Title = $"Unscoped notebook {Guid.NewGuid():N}",
                Slug = $"unscoped-nb-{Guid.NewGuid():N}",
                ProjectId = _fixture.ProjectId,
                GuideId = null
            };
            db.Notebooks.Add(notebook);
            await db.SaveChangesAsync();
            notebookId = notebook.Id;
        }

        var response = await Client!.GetAsync(
            $"/api/system-guide/sandbox-admin/requirements?projectId={_fixture.ProjectId:D}&notebookId={notebookId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ToLowerInvariant().Should().Contain("not associated with a guide");
        body.ToLowerInvariant().Should().NotContain("nullable object must have a value");
    }

    private async Task<SystemGuideFixture> SeedSystemGuideFixtureAsync()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IGuideAntsSystemSettingsStore>();

        var project = new Project
        {
            Title = SystemProjectTitle,
            Slug = SystemProjectSlug,
            Description = string.Empty,
            IsSystemProject = true
        };
        db.Projects.Add(project);

        var userGuide = new Assistant
        {
            Name = UserGuideName,
            Kind = AssistantKind.Guide,
            IsGlobal = true,
            IsActive = true,
            ModelId = "gpt-4.1"
        };
        var adminGuide = new Assistant
        {
            Name = AdminGuideName,
            Kind = AssistantKind.Guide,
            IsGlobal = true,
            IsActive = true,
            ModelId = "gpt-4.1"
        };
        db.Assistants.AddRange(userGuide, adminGuide);
        await db.SaveChangesAsync();

        var userNotebook = new Notebook
        {
            Title = "User Notebook",
            Slug = $"user-nb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = userGuide.Id
        };
        var adminNotebook = new Notebook
        {
            Title = "Admin Notebook",
            Slug = $"admin-nb-{Guid.NewGuid():N}",
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
            DisplayMode = "full",
            CommandMode = true,
            ShowTurnNavigation = true,
            Collapsible = false,
            AuthMode = PublishedGuideAuthMode.AppIdentity
        };
        var adminPublished = new PublishedGuide
        {
            GuideId = adminGuide.Id,
            NotebookId = adminNotebook.Id,
            Active = true,
            DisplayMode = "full",
            CommandMode = true,
            ShowTurnNavigation = true,
            Collapsible = false,
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
        await settingsStore.SaveAsync(settings);

        return new SystemGuideFixture(
            project.Id,
            userPublished.Id,
            adminPublished.Id);
    }

    private sealed record SystemGuideFixture(
        Guid ProjectId,
        Guid UserPublishedGuideId,
        Guid AdminPublishedGuideId);
}
