using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.PathGuard;

[TestClass]
public sealed class PathGuardMountTests
{
    private string _storageRoot = null!;
    private string _hostMountsRoot = null!;
    private NotebookStorageFixture _notebook = null!;

    [TestInitialize]
    public void SetUp()
    {
        if (!MountTestHelper.CanCreateDirectoryLinks)
        {
            Assert.Inconclusive("This machine cannot create directory links required for mount path-guard tests.");
        }

        _storageRoot = Path.Combine(Path.GetTempPath(), "script-agent-pathguard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
        _hostMountsRoot = MountTestHelper.CreateHostMountsRoot(_storageRoot);
        _notebook = new NotebookStorageFixture(_storageRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_storageRoot))
        {
            try
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [TestMethod]
    public void TryResolveAndAuthorizePath_allows_registered_writable_mount_for_read_and_write()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_notebook, _hostMountsRoot, "Shared", "shared-key", writable: true);
        var workingDirectory = Path.Combine(mount.NotebookScopedPath, "nested");

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            workingDirectory,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Read,
            out var authorizedReadPath,
            out var notebookRoot,
            out var readReason).Should().BeTrue(readReason);

        authorizedReadPath.Should().Be(
            Path.GetFullPath(Path.Combine(mount.ContainerSourcePath, "nested"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        notebookRoot.Should().Be(_notebook.NotebookRoot);

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            workingDirectory,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Write,
            out var authorizedWritePath,
            out _,
            out var writeReason).Should().BeTrue(writeReason);

        authorizedWritePath.Should().Be(authorizedReadPath);
    }

    [TestMethod]
    public void TryResolveAndAuthorizePath_rejects_unregistered_reparse_point()
    {
        MountTestHelper.CreateUnregisteredDirectoryLink(_notebook, _hostMountsRoot, "Evil", "evil-key");
        var candidate = Path.Combine(_notebook.NotebookRoot, "Evil");

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            candidate,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Read,
            out _,
            out _,
            out var reason).Should().BeFalse();

        reason.Should().Contain("unregistered reparse point");
    }

    [TestMethod]
    public void TryResolveAndAuthorizePath_rejects_write_through_read_only_registered_mount()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_notebook, _hostMountsRoot, "ReadOnly", "read-only-key", writable: false);
        var candidate = Path.Combine(mount.NotebookScopedPath, "Output");

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            candidate,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Write,
            out _,
            out _,
            out var reason).Should().BeFalse();

        reason.Should().Contain("read-only");
    }

    [TestMethod]
    public void TryResolveAndAuthorizePath_rejects_traversal_escape_via_parent_segments()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_notebook, _hostMountsRoot, "Shared", "shared-escape", writable: true);
        var candidate = Path.Combine(mount.NotebookScopedPath, "..", "..", "..", "escape-target");

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            candidate,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Read,
            out _,
            out _,
            out var reason).Should().BeFalse();

        reason.Should().Match(reasonText =>
            reasonText.Contains("notebook root", StringComparison.Ordinal) ||
            reasonText.Contains("notebook-scoped", StringComparison.Ordinal) ||
            reasonText.Contains("FILE_STORAGE_ROOT", StringComparison.Ordinal) ||
            reasonText.Contains("authorized scope", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryResolveAndAuthorizePath_rejects_malformed_mounts_json()
    {
        var mount = MountTestHelper.CreateRegisteredMount(_notebook, _hostMountsRoot, "Shared", "shared-malformed", writable: true);
        File.WriteAllText(Path.Combine(_notebook.NotebookRoot, ".guideants", "mounts.json"), "{ not-json");

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            mount.NotebookScopedPath,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Read,
            out _,
            out _,
            out var reason).Should().BeFalse();

        reason.Should().Contain("mounts registry");
    }

    [TestMethod]
    public void TryResolveAndAuthorizePath_rejects_registered_link_when_mounts_json_missing()
    {
        MountTestHelper.CreateUnregisteredDirectoryLink(_notebook, _hostMountsRoot, "Shared", "shared-missing-registry");
        var candidate = Path.Combine(_notebook.NotebookRoot, "Shared");

        global::ScriptExecutionAgent.PathGuard.TryResolveAndAuthorizePath(
            _storageRoot,
            candidate,
            _notebook.ProjectId,
            _notebook.NotebookId,
            PathAccessMode.Read,
            out _,
            out _,
            out var reason).Should().BeFalse();

        reason.Should().Contain("unregistered reparse point");
    }
}
