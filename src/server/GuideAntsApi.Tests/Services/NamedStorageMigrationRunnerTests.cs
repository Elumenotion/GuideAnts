using FluentAssertions;
using GuideAntsApi.Services.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NamedStorageMigrationRunnerTests
{
    [TestMethod]
    public void TryRewritePath_RewritesLegacyProjectAndNotebookRoots()
    {
        var storageRoot = @"D:\data\content-files";
        var projectId = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        var notebookId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var runner = new NamedStorageMigrationRunner("Server=(local)", storageRoot, NullLogger.Instance);
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "my-project");
        var notebooks = new Dictionary<Guid, string> { [notebookId] = "my-notebook" };

        var path = $@"{storageRoot}\{projectId}\notebooks\{notebookId}\docs\hello.md";
        var changed = runner.TryRewritePath(path, project, notebooks, out var rewritten);

        changed.Should().BeTrue();
        rewritten.Should().Be($@"{storageRoot}\my-project\my-notebook\docs\hello.md");
    }

    [TestMethod]
    public void TryRewritePath_RewritesLegacyProjectsCasNotebookMarkdownPath()
    {
        var storageRoot = "/app/content-files";
        var projectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var notebookId = Guid.Parse("66666666-7777-8888-9999-000000000000");
        var runner = new NamedStorageMigrationRunner("Server=(local)", storageRoot, NullLogger.Instance);
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "project-slug");
        var notebooks = new Dictionary<Guid, string> { [notebookId] = "notebook-slug" };

        var path = $"/app/content-files/projects/{projectId}/notebooks/{notebookId}/markdown/aa/bb/hash.md";
        var changed = runner.TryRewritePath(path, project, notebooks, out var rewritten);

        changed.Should().BeTrue();
        rewritten.Should().Be("/app/content-files/projects/project-slug/notebook-slug/markdown/aa/bb/hash.md");
    }

    [TestMethod]
    public void TryRewritePath_ReturnsFalse_WhenPathDoesNotContainLegacySegments()
    {
        var storageRoot = @"D:\data\content-files";
        var projectId = Guid.NewGuid();
        var runner = new NamedStorageMigrationRunner("Server=(local)", storageRoot, NullLogger.Instance);
        var project = new NamedStorageMigrationRunner.ProjectInfo(projectId, "project-slug");
        var notebooks = new Dictionary<Guid, string>();
        var path = @"D:\elsewhere\untouched\file.md";

        var changed = runner.TryRewritePath(path, project, notebooks, out var rewritten);

        changed.Should().BeFalse();
        rewritten.Should().Be(path);
    }
}
