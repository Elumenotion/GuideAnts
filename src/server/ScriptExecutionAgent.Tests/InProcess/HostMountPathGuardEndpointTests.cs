using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class HostMountPathGuardEndpointTests
{
    private ScriptExecutionAgentWebApplicationFactory _factory = null!;
    private string _hostMountsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        if (!MountTestHelper.CanCreateDirectoryLinks)
        {
            Assert.Inconclusive("This machine cannot create directory links required for mount endpoint tests.");
        }

        _factory = new ScriptExecutionAgentWebApplicationFactory();
        using (_factory.CreateClient())
        {
            // Force WebApplicationFactory host initialization so Notebook is created.
        }

        _hostMountsRoot = MountTestHelper.CreateHostMountsRoot(_factory.StorageRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Execute_succeeds_under_registered_writable_mapped_folder()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_factory.Notebook, _hostMountsRoot, "Shared", "shared-exec", writable: true);
        var workingDirectory = Path.Combine(mount.NotebookScopedPath, "Run");
        Directory.CreateDirectory(workingDirectory);

        using var client = _factory.CreateAuthenticatedClient();
        var body = new
        {
            script = OperatingSystem.IsWindows() ? "Write-Output 'mounted-ok'" : "echo mounted-ok",
            scriptType = OperatingSystem.IsWindows() ? 1 : 0,
            workingDirectory,
            projectId = _factory.Notebook.ProjectId.ToString(),
            notebookId = _factory.Notebook.NotebookId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("mounted-ok");
    }

    [TestMethod]
    public async Task Files_succeeds_under_registered_writable_mapped_folder()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_factory.Notebook, _hostMountsRoot, "Shared", "shared-files", writable: true);
        File.WriteAllText(Path.Combine(mount.ContainerSourcePath, "from-host.txt"), "hello");

        using var client = _factory.CreateAuthenticatedClient();
        var url =
            $"/files?directory={Uri.EscapeDataString(mount.NotebookScopedPath)}&projectId={_factory.Notebook.ProjectId}&notebookId={_factory.Notebook.NotebookId}";

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var files = await response.Content.ReadFromJsonAsync<string[]>();
        files.Should().NotBeNull();
        files!.Should().Contain("from-host.txt");
    }

    [TestMethod]
    public async Task Execute_rejects_unregistered_symlink_under_notebook_root()
    {
        MountTestHelper.CreateUnregisteredDirectoryLink(_factory.Notebook, _hostMountsRoot, "Evil", "evil-exec");
        var workingDirectory = Path.Combine(_factory.Notebook.NotebookRoot, "Evil");

        using var client = _factory.CreateAuthenticatedClient();
        var body = new
        {
            script = "echo should-not-run",
            scriptType = 0,
            workingDirectory,
            projectId = _factory.Notebook.ProjectId.ToString(),
            notebookId = _factory.Notebook.NotebookId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unregistered reparse point");
    }

    [TestMethod]
    public async Task Files_rejects_unregistered_symlink_under_notebook_root()
    {
        MountTestHelper.CreateUnregisteredDirectoryLink(_factory.Notebook, _hostMountsRoot, "Evil", "evil-files");
        var directory = Path.Combine(_factory.Notebook.NotebookRoot, "Evil");
        using var client = _factory.CreateAuthenticatedClient();
        var url =
            $"/files?directory={Uri.EscapeDataString(directory)}&projectId={_factory.Notebook.ProjectId}&notebookId={_factory.Notebook.NotebookId}";

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unregistered reparse point");
    }

    [TestMethod]
    public async Task Execute_rejects_write_through_read_only_registered_mount()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_factory.Notebook, _hostMountsRoot, "ReadOnly", "read-only-exec", writable: false);
        var workingDirectory = Path.Combine(mount.NotebookScopedPath, "Run");

        using var client = _factory.CreateAuthenticatedClient();
        var body = new
        {
            script = "echo should-not-run",
            scriptType = 0,
            workingDirectory,
            projectId = _factory.Notebook.ProjectId.ToString(),
            notebookId = _factory.Notebook.NotebookId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("read-only");
    }

    [TestMethod]
    public async Task Execute_rejects_when_mounts_json_is_malformed()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_factory.Notebook, _hostMountsRoot, "Shared", "shared-malformed-exec", writable: true);
        File.WriteAllText(Path.Combine(_factory.Notebook.NotebookRoot, ".guideants", "mounts.json"), "{ not-json");

        using var client = _factory.CreateAuthenticatedClient();
        var body = new
        {
            script = "echo should-not-run",
            scriptType = 0,
            workingDirectory = mount.NotebookScopedPath,
            projectId = _factory.Notebook.ProjectId.ToString(),
            notebookId = _factory.Notebook.NotebookId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("mounts registry");
    }
}
