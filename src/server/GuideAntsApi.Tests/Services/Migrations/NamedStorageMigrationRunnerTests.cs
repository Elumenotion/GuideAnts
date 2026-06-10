using GuideAntsApi.Services.Migrations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Migrations;

[TestClass]
public sealed class NamedStorageMigrationRunnerTests
{
    private const string StorageRoot = "/srv/storage";

    private static NamedStorageMigrationRunner CreateRunner(string storageRoot = StorageRoot) =>
        new("Server=unused;Database=unused;", storageRoot, NullLogger.Instance);

    [TestMethod]
    public async Task RunAsync_MissingStorageRoot_ThrowsDirectoryNotFound()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "nsm-missing-" + Guid.NewGuid().ToString("N"));
        var runner = CreateRunner(missingRoot);

        Func<Task> act = () => runner.RunAsync(apply: false);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [TestMethod]
    public void TryRewritePath_NullOrWhitespace_ReturnsFalse()
    {
        var runner = CreateRunner();
        var project = new NamedStorageMigrationRunner.ProjectInfo(Guid.NewGuid(), "alpha");
        var empty = new Dictionary<Guid, string>();

        runner.TryRewritePath(null, project, empty, out var fromNull).Should().BeFalse();
        fromNull.Should().BeEmpty();

        runner.TryRewritePath("   ", project, empty, out var fromBlank).Should().BeFalse();
        fromBlank.Should().Be("   ");
    }

    [TestMethod]
    public void TryRewritePath_UnrelatedPath_ReturnsFalse()
    {
        var runner = CreateRunner();
        var project = new NamedStorageMigrationRunner.ProjectInfo(Guid.NewGuid(), "alpha");

        var changed = runner.TryRewritePath(
            "/somewhere/else/file.md",
            project,
            new Dictionary<Guid, string>(),
            out var rewritten);

        changed.Should().BeFalse();
        rewritten.Should().Be("/somewhere/else/file.md");
    }

    [TestMethod]
    public void TryRewritePath_ProjectGuidRoot_RewritesToSlug()
    {
        var runner = CreateRunner();
        var projectId = Guid.NewGuid();
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "alpha");
        var input = Path.Combine(StorageRoot, projectId.ToString(), "doc", "file.md");

        var changed = runner.TryRewritePath(input, project, new Dictionary<Guid, string>(), out var rewritten);

        changed.Should().BeTrue();
        rewritten.Should().Be(Path.Combine(StorageRoot, "alpha", "doc", "file.md"));
    }

    [TestMethod]
    public void TryRewritePath_CasGuidRoot_RewritesToSlug()
    {
        var runner = CreateRunner();
        var projectId = Guid.NewGuid();
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "alpha");
        var input = Path.Combine(StorageRoot, "projects", projectId.ToString(), "blob.bin");

        var changed = runner.TryRewritePath(input, project, new Dictionary<Guid, string>(), out var rewritten);

        changed.Should().BeTrue();
        rewritten.Should().Be(Path.Combine(StorageRoot, "projects", "alpha", "blob.bin"));
    }

    [TestMethod]
    public void TryRewritePath_NotebookGuidRoot_RewritesProjectAndNotebookSlugs()
    {
        var runner = CreateRunner();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "alpha");
        var notebooks = new Dictionary<Guid, string> { [notebookId] = "notes" };
        var input = Path.Combine(StorageRoot, projectId.ToString(), "notebooks", notebookId.ToString(), "n.md");

        var changed = runner.TryRewritePath(input, project, notebooks, out var rewritten);

        changed.Should().BeTrue();
        rewritten.Should().Be(Path.Combine(StorageRoot, "alpha", "notes", "n.md"));
    }

    [TestMethod]
    public void TryRewritePath_ForwardSlashSeparators_AreRewritten()
    {
        var runner = CreateRunner();
        var projectId = Guid.NewGuid();
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "alpha");
        var input = $"{StorageRoot}/{projectId}/doc/file.md";

        var changed = runner.TryRewritePath(input, project, new Dictionary<Guid, string>(), out var rewritten);

        changed.Should().BeTrue();
        rewritten.Should().Be($"{StorageRoot}/alpha/doc/file.md");
    }
}
