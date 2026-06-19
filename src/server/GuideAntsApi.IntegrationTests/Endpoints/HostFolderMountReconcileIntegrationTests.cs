using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class HostFolderMountReconcileIntegrationTests : BaseEndpointTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string _storageRoot = null!;
    private static string _hostMountsRoot = null!;
    private Guid _projectId;
    private Guid _existingNotebookId;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        _storageRoot = CreateTempDirectory("storage");
        _hostMountsRoot = CreateTempDirectory("host-mounts");
        SharedFactory = new HostMountReconcileTestWebApplicationFactory(_storageRoot, _hostMountsRoot);
        await SharedFactory.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
        TryDeleteDirectory(_storageRoot);
        TryDeleteDirectory(_hostMountsRoot);
    }

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication(Role.Admin);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var project = new Project
        {
            Title = "Reconcile Test Project",
            Description = "Integration test project for mount reconciliation"
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        _projectId = project.Id;

        var notebook = new Notebook
        {
            ProjectId = _projectId,
            Title = "Existing Notebook",
            Slug = $"existing-nb-{Guid.NewGuid():N}"
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        _existingNotebookId = notebook.Id;
    }

    [TestMethod]
    public async Task RemoveCommand_UnlinksSymlinksBeforeReturningCommand()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var hostPath = CreateTempHostPath();
        var mountId = await CreateProjectMountAsync(hostPath, "RemoveFlow");
        await CreatePhysicalMountSourceAsync(mountId);
        await ReconcileMountAsync(mountId);

        using var scope = SharedFactory!.Services.CreateScope();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IStoragePathResolver>();
        var notebookRoot = pathResolver.GetNotebookRootPath(_projectId, _existingNotebookId);
        var linkPath = Path.Combine(notebookRoot, "RemoveFlow");
        Directory.Exists(linkPath).Should().BeTrue();

        var response = await Client.PostAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{mountId}/commands/remove",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<HostFolderMountRemoveEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.Status.Should().Be(HostFolderMountStatus.PendingRemoval);
        body.Command.Should().Contain("remove");

        Directory.Exists(linkPath).Should().BeFalse();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var link = await db.HostFolderMountLinks
            .SingleAsync(l => l.HostFolderMountId == mountId && l.NotebookId == _existingNotebookId);
        link.Status.Should().Be(HostFolderMountLinkStatus.Unlinked);
    }

    [TestMethod]
    public async Task RemoveFlow_ReconcileAfterSourceRemoved_MarksRemoved()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var hostPath = CreateTempHostPath();
        var mountId = await CreateProjectMountAsync(hostPath, "RemovedAfter");
        await CreatePhysicalMountSourceAsync(mountId);
        await ReconcileMountAsync(mountId);

        var removeResponse = await Client.PostAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{mountId}/commands/remove",
            content: null);
        removeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mount = await db.HostFolderMounts.AsNoTracking().SingleAsync(m => m.Id == mountId);
        var sourcePath = Path.Combine(_hostMountsRoot, mount.MountKey);
        Directory.Exists(sourcePath).Should().BeTrue();
        Directory.Delete(sourcePath, recursive: true);

        await ReconcileMountAsync(mountId);

        var reconciled = await db.HostFolderMounts.AsNoTracking().SingleAsync(m => m.Id == mountId);
        reconciled.Status.Should().Be(HostFolderMountStatus.Removed);
        reconciled.RemovedUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task NewNotebookWithActiveProjectMount_GetsLinkSymlinkAndRegistry()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var hostPath = CreateTempHostPath();
        var mountId = await CreateProjectMountAsync(hostPath, "SharedData");
        await CreatePhysicalMountSourceAsync(mountId);
        await ReconcileMountAsync(mountId);

        var guideId = await GetDefaultGuideIdAsync();
        var createNotebookResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/notebooks",
            new { title = "Second Notebook", guideId });
        createNotebookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdNotebook = await createNotebookResponse.Content.ReadFromJsonAsync<NotebookDto>(JsonOptions);
        createdNotebook.Should().NotBeNull();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IStoragePathResolver>();

        var link = await db.HostFolderMountLinks
            .SingleAsync(l => l.HostFolderMountId == mountId && l.NotebookId == createdNotebook!.Id);
        link.Status.Should().Be(HostFolderMountLinkStatus.Linked);

        var notebookRoot = pathResolver.GetNotebookRootPath(_projectId, createdNotebook!.Id);
        var linkPath = Path.Combine(notebookRoot, "SharedData");
        Directory.Exists(linkPath).Should().BeTrue();
        File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();

        var registryPath = Path.Combine(notebookRoot, ".guideants", "mounts.json");
        File.Exists(registryPath).Should().BeTrue();
        var registryJson = await File.ReadAllTextAsync(registryPath);
        registryJson.Should().Contain(mountId.ToString());
    }

    [TestMethod]
    public async Task NewNotebookWithSourceAbsent_LinkPendingRestartOrLinkErrorSurfaced()
    {
        var hostPath = CreateTempHostPath();
        var mountId = await CreateProjectMountAsync(hostPath, "AbsentSource");

        var guideId = await GetDefaultGuideIdAsync();
        var createNotebookResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/notebooks",
            new { title = "Notebook Missing Source", guideId });
        createNotebookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdNotebook = await createNotebookResponse.Content.ReadFromJsonAsync<NotebookDto>(JsonOptions);
        createdNotebook.Should().NotBeNull();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IStoragePathResolver>();

        var link = await db.HostFolderMountLinks
            .SingleAsync(l => l.HostFolderMountId == mountId && l.NotebookId == createdNotebook!.Id);
        link.Status.Should().BeOneOf(
            HostFolderMountLinkStatus.PendingRestart,
            HostFolderMountLinkStatus.LinkError);
        link.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        link.ErrorMessage.Should().Contain("Missing source");

        var notebookRoot = pathResolver.GetNotebookRootPath(_projectId, createdNotebook!.Id);
        Directory.Exists(Path.Combine(notebookRoot, "AbsentSource")).Should().BeFalse();
    }

    [TestMethod]
    public async Task StartupReconciliation_RecreatesMissingSymlinkForActiveMount()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var hostPath = CreateTempHostPath();
        var mountId = await CreateProjectMountAsync(hostPath, "RecreateMe");
        await CreatePhysicalMountSourceAsync(mountId);
        await ReconcileMountAsync(mountId);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IStoragePathResolver>();
        var mountService = scope.ServiceProvider.GetRequiredService<IHostFolderMountService>();

        var notebookRoot = pathResolver.GetNotebookRootPath(_projectId, _existingNotebookId);
        var linkPath = Path.Combine(notebookRoot, "RecreateMe");
        Directory.Exists(linkPath).Should().BeTrue();
        Directory.Delete(linkPath);

        await mountService.ReconcileAllMountsAsync();

        Directory.Exists(linkPath).Should().BeTrue();
        File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();

        var link = await db.HostFolderMountLinks
            .SingleAsync(l => l.HostFolderMountId == mountId && l.NotebookId == _existingNotebookId);
        link.Status.Should().Be(HostFolderMountLinkStatus.Linked);
    }

    private async Task<Guid> CreateProjectMountAsync(string hostPath, string leafName)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/host-folder-mounts",
            new HostFolderMountCreateEndpointRequest
            {
                Scope = HostFolderMountScope.Project.ToString(),
                HostPath = hostPath,
                LeafName = leafName
            });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<HostFolderMountCreateEndpointResponse>(JsonOptions);
        body.Should().NotBeNull();
        return body!.MountId;
    }

    private async Task CreatePhysicalMountSourceAsync(Guid mountId)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mount = await db.HostFolderMounts.AsNoTracking().SingleAsync(m => m.Id == mountId);
        var physicalPath = Path.Combine(_hostMountsRoot, mount.MountKey);
        Directory.CreateDirectory(physicalPath);
        await File.WriteAllTextAsync(Path.Combine(physicalPath, "probe.txt"), "ok");
    }

    private async Task ReconcileMountAsync(Guid mountId)
    {
        var response = await Client.PostAsync(
            $"/api/projects/{_projectId}/host-folder-mounts/{mountId}/reconcile",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string CreateTempHostPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guideants-host-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateTempDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"guideants-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool CanCreateDirectorySymlinks()
    {
        var baseDir = CreateTempDirectory("symlink-probe");
        var targetDir = Path.Combine(baseDir, "target");
        var linkDir = Path.Combine(baseDir, "link");
        Directory.CreateDirectory(targetDir);

        try
        {
            Directory.CreateSymbolicLink(linkDir, targetDir);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDeleteDirectory(baseDir);
        }
    }

    private sealed class HostMountReconcileTestWebApplicationFactory : TestWebApplicationFactory
    {
        private readonly string _storageRoot;
        private readonly string _hostMountsRoot;

        public HostMountReconcileTestWebApplicationFactory(string storageRoot, string hostMountsRoot)
        {
            _storageRoot = storageRoot;
            _hostMountsRoot = hostMountsRoot;
        }

        protected override void ConfigureTestAppConfiguration(IConfigurationBuilder config, WebHostBuilderContext context)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = _storageRoot,
                ["FileStorage:HostMountsRoot"] = _hostMountsRoot
            });
        }
    }
}
