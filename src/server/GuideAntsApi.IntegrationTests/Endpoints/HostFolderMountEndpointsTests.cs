using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class HostFolderMountEndpointsTests : BaseEndpointTest
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
            Title = "Host Mount Test Project",
            Description = "Integration test project for host folder mounts"
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        _projectId = project.Id;

        var notebook = new Notebook
        {
            ProjectId = _projectId,
            Title = "Host Mount Notebook",
            Slug = $"host-mount-nb-{Guid.NewGuid():N}"
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        _notebookId = notebook.Id;
    }

    [TestMethod]
    public async Task CreateMount_ProjectScope_ReturnsPlanSection11Shape()
    {
        var hostPath = CreateTempHostPath();
        var response = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = hostPath,
                LeafName = "SharedData"
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.MountId.Should().NotBeEmpty();
        body.Status.Should().Be(HostFolderMountStatus.PendingRestart);
        body.LeafName.Should().Be("SharedData");
        body.ContainerSourcePath.Should().StartWith("/app/HostMounts/");
        body.Command.Should().Contain("guideants-host-mount");
        body.Command.Should().Contain(body.MountId.ToString());
        body.Command.Should().Contain(hostPath);
    }

    [TestMethod]
    public async Task CreateMount_WindowsStyleHostPath_AcceptedWhenApiRunsOnLinux()
    {
        var hostPath = $@"D:\guideants-host-mount-{Guid.NewGuid():N}";
        var response = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = hostPath,
                LeafName = "WindowsHost"
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.LeafName.Should().Be("WindowsHost");
        body.Command.Should().Contain(hostPath);
    }

    [TestMethod]
    public async Task CreateMount_NotebookScope_CreatesSingleLinkRow()
    {
        var hostPath = CreateTempHostPath();
        var response = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Notebook.ToString(),
                NotebookId = _notebookId,
                HostPath = hostPath,
                LeafName = "NotebookOnly"
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var links = await db.HostFolderMountLinks
            .Where(link => link.HostFolderMountId == body!.MountId)
            .ToListAsync();
        links.Should().HaveCount(1);
        links[0].NotebookId.Should().Be(_notebookId);
    }

    [TestMethod]
    public async Task NonAdmin_CreateMount_ReturnsForbidden()
    {
        SetupAuthentication(Role.Reader);

        var response = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = CreateTempHostPath(),
                LeafName = "Forbidden"
            });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task CreateMount_InvalidLeafName_ReturnsBadRequest()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = CreateTempHostPath(),
                LeafName = "Output"
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions);
        payload.Should().ContainKey("message");
        payload!["message"].Should().Contain("reserved");
    }

    [TestMethod]
    public async Task ListAndGet_OmitSensitiveFieldsInSummary_IncludeInAdminDetail()
    {
        var hostPath = CreateTempHostPath();
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = hostPath,
                LeafName = "ListGetTest"
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);

        var listResponse = await Client.GetAsync($"/api/projects/{_projectId}/host-folder-mounts");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<List<HostFolderMountSummaryDto>>(JsonOptions);
        list.Should().NotBeNull();
        list!.Should().Contain(item => item.MountId == created!.MountId);
        var summaryJson = await listResponse.Content.ReadAsStringAsync();
        summaryJson.Should().NotContain(hostPath);

        var getResponse = await Client.GetAsync($"/api/projects/{_projectId}/host-folder-mounts/{created!.MountId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await getResponse.Content.ReadFromJsonAsync<HostFolderMountDetailDto>(JsonOptions);
        detail.Should().NotBeNull();
        detail!.HostPath.Should().Be(hostPath);
    }

    [TestMethod]
    public async Task ApplyCommand_ReturnsSanitizedCommandShape()
    {
        var hostPath = CreateTempHostPath();
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = hostPath,
                LeafName = "ApplyCmd"
            });
        var created = await createResponse.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);

        var response = await Client.PostAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{created!.MountId}/commands/apply",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountApplyCommandEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.Command.Should().Be(created.Command);
        body.Command.Should().Contain("apply");
        body.ContainerSourcePath.Should().Be(created.ContainerSourcePath);
    }

    [TestMethod]
    public async Task RemoveCommand_ReturnsPlanSection11Shape()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = CreateTempHostPath(),
                LeafName = "RemoveCmd"
            });
        var created = await createResponse.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);

        var response = await Client.PostAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{created!.MountId}/commands/remove",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountRemoveEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.MountId.Should().Be(created.MountId);
        body.Status.Should().Be(HostFolderMountStatus.PendingRemoval);
        body.Command.Should().Contain("remove");
        body.Command.Should().Contain(created.MountId.ToString());
    }

    [TestMethod]
    public async Task Reconcile_ReturnsCurrentStatus()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = CreateTempHostPath(),
                LeafName = "ReconcileMe"
            });
        var created = await createResponse.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);

        var response = await Client.PostAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{created!.MountId}/reconcile",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountReconcileEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.MountId.Should().Be(created.MountId);
        body.Status.Should().Be(HostFolderMountStatus.PendingRestart);
        body.Message.Should().Contain("Missing source");
    }

    [TestMethod]
    public async Task DeleteMount_RemovesRecord()
    {
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = CreateTempHostPath(),
                LeafName = "DeleteMe"
            });
        var created = await createResponse.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);

        var deleteResponse = await Client.DeleteAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{created!.MountId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await Client.GetAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{created.MountId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task InternalComposeOverridePlan_ReturnsScriptShape()
    {
        var hostPath = CreateTempHostPath();
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = hostPath,
                LeafName = "InternalPlan"
            });
        var created = await createResponse.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);

        var response = await Client.GetAsync(
            $"/api/internal/host-folder-mounts/{created!.MountId}/compose-override-plan");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<HostFolderMountComposeOverridePlanEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.MountId.Should().Be(created.MountId);
        body.ProjectId.Should().Be(_projectId);
        body.MountKey.Should().NotBeNullOrWhiteSpace();
        body.SourceKind.Should().Be(SourceKind.LocalPath.ToString());
        body.HostPath.Should().Be(HostFolderMountCommandTextBuilder.FormatHostPathForCompose(hostPath));
    }

    private static string CreateTempHostPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guideants-host-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
