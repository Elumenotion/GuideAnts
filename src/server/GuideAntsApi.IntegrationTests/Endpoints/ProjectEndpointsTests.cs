using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class ProjectEndpointsTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication();
    }

    [TestMethod]
    public async Task CreateProject_WithValidData_ReturnsCreatedProject()
    {
        // Arrange
        var createDto = new CreateProjectDto("Integration Test Project", "Test Description");

        // Act
        var response = await Client!.PostAsJsonAsync("/api/projects", createDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await response.Content.ReadFromJsonAsync<ProjectDto>();
        project.Should().NotBeNull();
        project!.Title.Should().Be("Integration Test Project");
        project.Description.Should().Be("Test Description");
    }

    [TestMethod]
    public async Task GetProject_WhenExists_ReturnsProject()
    {
        // Arrange
        var createDto = new CreateProjectDto("Test Project", "Description");
        var createResponse = await Client!.PostAsJsonAsync("/api/projects", createDto);
        var createdProject = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();

        // Act
        var response = await Client.GetAsync($"/api/projects/{createdProject!.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var project = await response.Content.ReadFromJsonAsync<ProjectDto>();
        project.Should().NotBeNull();
        project!.Id.Should().Be(createdProject.Id);
        project.Title.Should().Be("Test Project");
    }

    [TestMethod]
    public async Task GetProject_WhenNotExists_ReturnsNotFound()
    {
        // Act
        var response = await Client!.GetAsync($"/api/projects/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task UpdateProject_WhenOwner_UpdatesProject()
    {
        // Arrange
        var createDto = new CreateProjectDto("Original Title", "Original Description");
        var createResponse = await Client!.PostAsJsonAsync("/api/projects", createDto);
        var createdProject = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();

        var updateDto = new UpdateProjectDto("Updated Title", "Updated Description");

        // Act
        var response = await Client.PutAsJsonAsync($"/api/projects/{createdProject!.Id}", updateDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var project = await response.Content.ReadFromJsonAsync<ProjectDto>();
        project.Should().NotBeNull();
        project!.Id.Should().Be(createdProject.Id);
        project.Title.Should().Be("Updated Title");
        project.Description.Should().Be("Updated Description");
    }

    [TestMethod]
    public async Task DeleteProject_WhenOwnerAndEmpty_RemovesProject()
    {
        // Arrange
        var createDto = new CreateProjectDto("Project to Delete", "Will be deleted");
        var createResponse = await Client!.PostAsJsonAsync("/api/projects", createDto);
        var createdProject = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();

        // Act
        var deleteResponse = await Client.DeleteAsync($"/api/projects/{createdProject!.Id}");
        var getResponse = await Client.GetAsync($"/api/projects/{createdProject.Id}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task GetUserProjects_ReturnsOnlyUserProjects()
    {
        // Arrange
        var projects = new[]
        {
            new CreateProjectDto("Project 1", "Description 1"),
            new CreateProjectDto("Project 2", "Description 2"),
            new CreateProjectDto("Project 3", "Description 3")
        };

        foreach (var project in projects)
        {
            await Client!.PostAsJsonAsync("/api/projects", project);
        }

        // Act
        var response = await Client.GetAsync("/api/projects");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var userProjects = await response.Content.ReadFromJsonAsync<IEnumerable<ProjectDto>>();
        userProjects.Should().NotBeNull();
        userProjects!.Should().HaveCount(3);
        userProjects.Select(p => p.Title).Should().BeEquivalentTo(
            new[] { "Project 1", "Project 2", "Project 3" });
    }

    [TestMethod]
    public async Task GetProjectDetails_ReturnsFullProjectDetails()
    {
        // Arrange
        var createDto = new CreateProjectDto("Detailed Project", "With full details");
        var createResponse = await Client!.PostAsJsonAsync("/api/projects", createDto);
        var createdProject = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();

        var beforeNotebookCreate = DateTime.UtcNow;
        var notebookResponse = await Client.PostAsJsonAsync($"/api/projects/{createdProject!.Id}/notebooks", new { title = "Notebook A", guideId = await GetDefaultGuideIdAsync() });
        notebookResponse.EnsureSuccessStatusCode();
        var createdNotebook = await notebookResponse.Content.ReadFromJsonAsync<NotebookDto>();

        // Act
        var response = await Client.GetAsync($"/api/projects/{createdProject.Id}/details");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var details = await response.Content.ReadFromJsonAsync<ProjectDetailsDto>();
        details.Should().NotBeNull();
        details!.Id.Should().Be(createdProject.Id);
        details.Title.Should().Be("Detailed Project");
        details.Description.Should().Be("With full details");
        details.Notebooks.Should().NotBeNull();
        details.ContentFiles.Should().NotBeNull();
        details.Links.Should().NotBeNull();
        details.SemiStructuredDatas.Should().NotBeNull();

        createdNotebook.Should().NotBeNull();
        var notebookDetails = details.Notebooks.First(n => n.Id == createdNotebook!.Id);
        notebookDetails.LastActivity.Should().BeOnOrAfter(beforeNotebookCreate);
    }

    [TestMethod]
    public async Task CopyProject_CreatesNewProjectWithSameContent()
    {
        // Arrange
        var createDto = new CreateProjectDto("Original Project", "To be copied");
        var createResponse = await Client!.PostAsJsonAsync("/api/projects", createDto);
        var originalProject = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();

        // Act
        var response = await Client.PostAsync($"/api/projects/{originalProject!.Id}/copy", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var copiedProject = await response.Content.ReadFromJsonAsync<ProjectDto>();
        copiedProject.Should().NotBeNull();
        copiedProject!.Id.Should().NotBe(originalProject.Id);
        copiedProject.Title.Should().Be($"Copy of {originalProject.Title}");
        copiedProject.Description.Should().Be(originalProject.Description);
    }
} 