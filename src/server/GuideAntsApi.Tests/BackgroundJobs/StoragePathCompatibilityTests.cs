using FluentAssertions;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class StoragePathCompatibilityTests
{
    [TestMethod]
    public void TryResolveExistingFilePath_Resolves_WindowsStyleRelativeContentFilesPath()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"wf-path-test-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(sandboxRoot, "ContentFiles");
        var projectId = Guid.NewGuid().ToString();
        var notebookId = Guid.NewGuid().ToString();
        var filePath = Path.Combine(storageRoot, "projects", projectId, "notebooks", notebookId, "markdown", "aa", "bb", "sample.md");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "hello");

            var storedPath = $"..\\ContentFiles\\projects\\{projectId}\\notebooks\\{notebookId}\\markdown\\aa\\bb\\sample.md";

            var resolved = StoragePathCompatibility.TryResolveExistingFilePath(storedPath, storageRoot, out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(filePath));
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryResolveExistingFilePath_Resolves_FromDifferentAbsoluteRootUsingContentFilesAnchor()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"wf-path-test-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(sandboxRoot, "ContentFiles");
        var projectId = Guid.NewGuid().ToString();
        var filePath = Path.Combine(storageRoot, "projects", projectId, "content", "aa", "bb", "doc.md");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "hello");

            var storedPath = $"C:\\legacy\\ContentFiles\\projects\\{projectId}\\content\\aa\\bb\\doc.md";

            var resolved = StoragePathCompatibility.TryResolveExistingFilePath(storedPath, storageRoot, out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(filePath));
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryResolveExistingFilePath_ReturnsFalse_WhenFileDoesNotExist()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"wf-path-test-{Guid.NewGuid():N}", "ContentFiles");
        var storedPath = "..\\ContentFiles\\projects\\missing\\notebooks\\missing\\markdown\\aa\\bb\\missing.md";

        var resolved = StoragePathCompatibility.TryResolveExistingFilePath(storedPath, storageRoot, out var resolvedPath);

        resolved.Should().BeFalse();
        resolvedPath.Should().BeEmpty();
    }

    [TestMethod]
    public void TryResolveExistingFilePath_Resolves_MigratedProjectGuidToSlugPath()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"wf-path-test-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(sandboxRoot, "ContentFiles");
        var legacyProjectId = Guid.NewGuid().ToString();
        var slug = "project-slug";
        var filePath = Path.Combine(storageRoot, "projects", slug, "content", "67", "11", "doc.md");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "hello");

            var storedPath = Path.Combine(storageRoot, "projects", legacyProjectId, "content", "67", "11", "doc.md");

            var resolved = StoragePathCompatibility.TryResolveExistingFilePath(storedPath, storageRoot, out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(filePath));
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryResolveExistingFilePath_Resolves_MigratedLegacyNotebookGuidMarkdownPath()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"wf-path-test-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(sandboxRoot, "ContentFiles");
        var legacyProjectId = Guid.NewGuid().ToString();
        var legacyNotebookId = Guid.NewGuid().ToString();
        var projectSlug = "project-slug";
        var notebookSlug = "notebook-slug";
        var filePath = Path.Combine(storageRoot, "projects", projectSlug, notebookSlug, "markdown", "09", "04", "note.md");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "hello");

            var storedPath = Path.Combine(
                storageRoot,
                "projects",
                legacyProjectId,
                "notebooks",
                legacyNotebookId,
                "markdown",
                "09",
                "04",
                "note.md");

            var resolved = StoragePathCompatibility.TryResolveExistingFilePath(storedPath, storageRoot, out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(filePath));
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryResolveExistingFilePath_Resolves_StaleProjectSlugToRenamedSlugPath()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"wf-path-test-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(sandboxRoot, "ContentFiles");
        var staleSlug = "doug-ware's-quick-start-project-4";
        var currentSlug = "doug-ware-x27xs-quick-start-project-4";
        var filePath = Path.Combine(storageRoot, "projects", currentSlug, "content", "67", "11", "doc.md");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "hello");

            var storedPath = Path.Combine(storageRoot, "projects", staleSlug, "content", "67", "11", "doc.md");

            var resolved = StoragePathCompatibility.TryResolveExistingFilePath(storedPath, storageRoot, out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(filePath));
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }
}
