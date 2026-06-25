using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class ProjectScheduledJobEndpointsTests : BaseEndpointTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private Guid _projectId;
    private Guid _notebookId;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication(Role.Admin);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var project = new Project
        {
            Title = "Scheduled Job Test Project",
            Description = "Integration test project for scheduled jobs"
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        _projectId = project.Id;

        var notebook = new Notebook
        {
            ProjectId = _projectId,
            Title = "Scheduled Job Notebook",
            Slug = $"scheduled-job-nb-{Guid.NewGuid():N}"
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        _notebookId = notebook.Id;
    }

    [TestMethod]
    public async Task CreateListUpdateDelete_NewConversationJob_SucceedsForAdmin()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs",
            BuildCreateRequest("Daily report"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectScheduledJobDetailDto>(JsonOptions);
        created.Should().NotBeNull();
        created!.JobType.Should().Be("NewConversation");
        created.NotebookId.Should().Be(_notebookId);

        var listResponse = await Client.GetAsync($"/api/projects/{_projectId}/scheduled-jobs");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listed = await listResponse.Content.ReadFromJsonAsync<List<ProjectScheduledJobSummaryDto>>(JsonOptions);
        listed.Should().NotBeNull();
        listed!.Should().ContainSingle(j => j.Id == created.Id);

        var updateResponse = await Client.PutAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs/{created.Id}",
            new UpdateProjectScheduledJobRequest(
                "Daily report updated",
                "NewConversation",
                _notebookId,
                true,
                "UTC",
                new FriendlyScheduleDto(ScheduleFrequency.Daily, "10:00", null, null, null, null),
                "Updated title",
                "Updated prompt",
                "assistant",
                null));

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await Client.DeleteAsync($"/api/projects/{_projectId}/scheduled-jobs/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task ScheduledJobEndpoints_RequireAdminRole()
    {
        SetupAuthentication(Role.Contributor);
        var contributorResponse = await Client.GetAsync($"/api/projects/{_projectId}/scheduled-jobs");
        contributorResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        SetupAuthentication(Role.Reader);
        var readerResponse = await Client.GetAsync($"/api/projects/{_projectId}/scheduled-jobs");
        readerResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        SetupAuthentication(Role.Admin);
        var adminResponse = await Client.GetAsync($"/api/projects/{_projectId}/scheduled-jobs");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task RunNow_ReturnsAccepted()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs",
            BuildCreateRequest("Run now job"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectScheduledJobDetailDto>(JsonOptions);

        var runResponse = await Client.PostAsync(
            $"/api/projects/{_projectId}/scheduled-jobs/{created!.Id}/run",
            null);
        runResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [TestMethod]
    public async Task ListRuns_ReturnsNotFoundWhenJobBelongsToDifferentProject()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs",
            BuildCreateRequest("Cross project job"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectScheduledJobDetailDto>(JsonOptions);

        var wrongProjectId = Guid.NewGuid();
        var response = await Client.GetAsync(
            $"/api/projects/{wrongProjectId}/scheduled-jobs/{created!.Id}/runs");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ListRuns_ClampsPagingBounds()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs",
            BuildCreateRequest("Paging job"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectScheduledJobDetailDto>(JsonOptions);

        var response = await Client.GetAsync(
            $"/api/projects/{_projectId}/scheduled-jobs/{created!.Id}/runs?page=0&pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<PagedProjectScheduledJobRunsDto>(JsonOptions);
        page.Should().NotBeNull();
        page!.Page.Should().Be(1);
        page.PageSize.Should().Be(100);
    }

    [TestMethod]
    public async Task GetRun_ReturnsNotFoundWhenJobBelongsToDifferentProject()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs",
            BuildCreateRequest("Run detail job"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectScheduledJobDetailDto>(JsonOptions);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var run = new ProjectScheduledJobRun
        {
            ScheduledJobId = created!.Id,
            TriggeredBy = ProjectScheduledJobTrigger.Manual,
            Status = ProjectScheduledJobRunStatus.Succeeded,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.ProjectScheduledJobRuns.Add(run);
        await db.SaveChangesAsync();

        var wrongProjectId = Guid.NewGuid();
        var response = await Client.GetAsync(
            $"/api/projects/{wrongProjectId}/scheduled-jobs/{created.Id}/runs/{run.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task GetJob_ReturnsNotFoundWhenJobBelongsToDifferentProject()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/scheduled-jobs",
            BuildCreateRequest("Wrong project job"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectScheduledJobDetailDto>(JsonOptions);

        var wrongProjectId = Guid.NewGuid();
        var response = await Client.GetAsync(
            $"/api/projects/{wrongProjectId}/scheduled-jobs/{created!.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private CreateProjectScheduledJobRequest BuildCreateRequest(string name) =>
        new(
            name,
            "NewConversation",
            _notebookId,
            true,
            "UTC",
            new FriendlyScheduleDto(ScheduleFrequency.Daily, "09:00", null, null, null, null),
            "Scheduled {timestamp}",
            "Summarize project activity.",
            "assistant",
            null);
}
