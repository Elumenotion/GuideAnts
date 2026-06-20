using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
            notebookId = _factory.Notebook.NotebookId.ToString(),
            guideId = _factory.Notebook.GuideId.ToString()
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
            notebookId = _factory.Notebook.NotebookId.ToString(),
            guideId = _factory.Notebook.GuideId.ToString()
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
            notebookId = _factory.Notebook.NotebookId.ToString(),
            guideId = _factory.Notebook.GuideId.ToString()
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
            notebookId = _factory.Notebook.NotebookId.ToString(),
            guideId = _factory.Notebook.GuideId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("mounts registry");
    }

    [TestMethod]
    public async Task Execute_under_mount_with_identity_isolation_completes_without_chowning_mount_tree()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only notebook identity isolation test.");
        }

        if (!MountTestHelper.CanUseLinuxIdentityIsolation)
        {
            Assert.Inconclusive(
                "Linux notebook identity isolation requires privileges to create system groups (groupadd/groupdel).");
        }

        using var factory = new ScriptExecutionAgentWebApplicationFactory(
            enableIdentityIsolation: true,
            allowOwnershipFallback: false);
        using (factory.CreateClient())
        {
            // Force host initialization.
        }

        var hostMountsRoot = MountTestHelper.CreateHostMountsRoot(factory.StorageRoot);
        var mount = MountTestHelper.CreateRegisteredMount(factory.Notebook, hostMountsRoot, "Shared", "shared-identity", writable: true);
        var workingDirectory = Path.Combine(mount.NotebookScopedPath, "Run");
        Directory.CreateDirectory(workingDirectory);

        var markerPath = Path.Combine(mount.ContainerSourcePath, "marker.txt");
        File.WriteAllText(markerPath, "seed");
        var markerOwnerBefore = GetFileOwner(markerPath);

        for (var index = 0; index < 200; index++)
        {
            Directory.CreateDirectory(Path.Combine(mount.ContainerSourcePath, $"bulk-{index:D3}"));
            File.WriteAllText(Path.Combine(mount.ContainerSourcePath, $"bulk-{index:D3}", "file.txt"), "x");
        }

        using var client = factory.CreateAuthenticatedClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = new
        {
            script = "echo mounted-identity-ok",
            scriptType = 0,
            workingDirectory,
            projectId = factory.Notebook.ProjectId.ToString(),
            notebookId = factory.Notebook.NotebookId.ToString(),
            guideId = factory.Notebook.GuideId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("mounted-identity-ok");
        GetFileOwner(markerPath).Should().Be(markerOwnerBefore);
    }

    [TestMethod]
    public async Task Execute_under_mount_with_identity_isolation_runs_in_compatibility_mode()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only notebook identity isolation test.");
        }

        using var factory = new ScriptExecutionAgentWebApplicationFactory(
            enableIdentityIsolation: true,
            allowOwnershipFallback: false);
        using (factory.CreateClient())
        {
            // Force host initialization.
        }

        var hostMountsRoot = MountTestHelper.CreateHostMountsRoot(factory.StorageRoot);
        var mount = MountTestHelper.CreateRegisteredMount(factory.Notebook, hostMountsRoot, "Shared", "shared-compat", writable: true);
        var workingDirectory = Path.Combine(mount.NotebookScopedPath, "Run");
        Directory.CreateDirectory(workingDirectory);

        using var client = factory.CreateAuthenticatedClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = new
        {
            script = "id -u",
            scriptType = 0,
            workingDirectory,
            projectId = factory.Notebook.ProjectId.ToString(),
            notebookId = factory.Notebook.NotebookId.ToString(),
            guideId = factory.Notebook.GuideId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body, cts.Token);
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var doc = JsonDocument.Parse(payload);
        var standardOutput = ReadStandardOutput(doc.RootElement);
        standardOutput.Should().NotBeNullOrWhiteSpace(payload);
        standardOutput!.Trim().Should().Be(GetCurrentUserId());
    }

    private static string? ReadStandardOutput(JsonElement root)
    {
        if (root.TryGetProperty("standardOutput", out var camel))
        {
            return camel.GetString();
        }

        if (root.TryGetProperty("StandardOutput", out var pascal))
        {
            return pascal.GetString();
        }

        if (root.TryGetProperty("stdout", out var shortName))
        {
            return shortName.GetString();
        }

        return null;
    }

    private static string GetFileOwner(string path)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "stat",
            Arguments = $"-c %u:%g {path}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start stat.");
        process.WaitForExit(5_000);
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
        return process.StandardOutput.ReadToEnd().Trim();
    }

    private static string GetCurrentUserId()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "id",
            Arguments = "-u",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start id.");
        process.WaitForExit(5_000);
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
        return process.StandardOutput.ReadToEnd().Trim();
    }
}
