using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Tests.TestUtils;
using GuideAntsApi.Services.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class NotebookFileServiceTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static IConfiguration CreateConfig(string storagePath)
    {
        var dict = new Dictionary<string, string?> { ["FileStorage:Path"] = storagePath };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IServiceScopeFactory CreateScopeFactory(ApplicationDbContext context)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(context);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);
        return scopeFactoryMock.Object;
    }

    private static NotebookFileService CreateService(ApplicationDbContext ctx, string storagePath)
    {
        var scopeFactory = CreateScopeFactory(ctx);
        var config = CreateConfig(storagePath);
        var markdown = Mock.Of<IMarkdownExtractionService>();

        var sync = NotebookFileSyncTestFactory.Create(scopeFactory);

        return new NotebookFileService(
            scopeFactory,
            config,
            sync,
            NullLogger<NotebookFileService>.Instance,
            Mock.Of<IFileLineageService>(),
            Mock.Of<IContentFileService>(),
            markdown);
    }

    private static IEnumerable<NotebookFileDto> EnumerateFilesRecursively(NotebookFolderTreeDto node)
    {
        foreach (var file in node.Files)
        {
            yield return file;
        }

        foreach (var folder in node.SubFolders)
        {
            foreach (var file in EnumerateFilesRecursively(folder))
            {
                yield return file;
            }
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nbfile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string NotebookRoot(string root, Guid projectId, Guid notebookId) =>
        Path.Combine(root, projectId.ToString(), "notebooks", notebookId.ToString());

    private static async Task<(Project Project, Notebook Notebook)> SeedProjectAndNotebook(ApplicationDbContext ctx)
    {
        var project = new Project { Id = Guid.NewGuid(), Title = "P" };
        var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
        ctx.Projects.Add(project);
        ctx.Notebooks.Add(notebook);
        await ctx.SaveChangesAsync();
        return (project, notebook);
    }

    private static void AddFileRow(ApplicationDbContext ctx, Guid notebookId, string relativePath, long size = 4)
    {
        ctx.NotebookFiles.Add(new NotebookFile
        {
            Id = Guid.NewGuid(),
            NotebookId = notebookId,
            RelativePath = relativePath,
            FileSize = size,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash",
            Created = DateTime.UtcNow
        });
    }

    private static bool TryCreateFileSymlink(string symlinkPath, string targetPath)
    {
        try
        {
            if (File.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
            }

            File.CreateSymbolicLink(symlinkPath, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [TestMethod]
    public async Task ListFilesAsync_FiltersHiddenTemporaryAndProtectedFiles()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            AddFileRow(ctx, notebook.Id, "visible.txt");
            AddFileRow(ctx, notebook.Id, "Resources/protected.png");
            AddFileRow(ctx, notebook.Id, ".guideants/notebook.json");
            AddFileRow(ctx, notebook.Id, "__pycache__/cache.pyc");
            AddFileRow(ctx, notebook.Id, "abcdef0123456789abcdef0123456789_script.py");
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var files = (await svc.ListFilesAsync(project.Id, notebook.Id)).ToList();

            files.Should().ContainSingle();
            files[0].RelativePath.Should().Be("visible.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListFilesAsync_HidesOutputProjectedResourceSymlinkFiles()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            var resourcesDir = Path.Combine(nbRoot, "Resources", "crew-Slide-Shows");
            var outputDir = Path.Combine(nbRoot, "Output");
            Directory.CreateDirectory(resourcesDir);
            Directory.CreateDirectory(outputDir);

            var resourcePath = Path.Combine(resourcesDir, "api.py");
            await File.WriteAllTextAsync(resourcePath, "print('bootstrap')");
            var projectedPath = Path.Combine(outputDir, "api.py");
            var relativeTarget = Path.Combine("..", "Resources", "crew-Slide-Shows", "api.py");
            if (!TryCreateFileSymlink(projectedPath, relativeTarget))
            {
                Assert.Inconclusive("File symlink creation is not available in this environment.");
            }

            var userVisiblePath = Path.Combine(outputDir, "user-visible.txt");
            await File.WriteAllTextAsync(userVisiblePath, "hello");

            AddFileRow(ctx, notebook.Id, "Resources/crew-Slide-Shows/api.py", new FileInfo(resourcePath).Length);
            AddFileRow(ctx, notebook.Id, "Output/api.py", new FileInfo(projectedPath).Length);
            AddFileRow(ctx, notebook.Id, "Output/user-visible.txt", new FileInfo(userVisiblePath).Length);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var files = (await svc.ListFilesAsync(project.Id, notebook.Id)).ToList();
            var relativePaths = files.Select(f => f.RelativePath).ToList();

            relativePaths.Should().Contain("Output/user-visible.txt");
            relativePaths.Should().NotContain("Output/api.py");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFolderTreeAsync_BuildsNestedFolderHierarchy()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            AddFileRow(ctx, notebook.Id, "rootfile.txt");
            AddFileRow(ctx, notebook.Id, "docs/readme.md");
            AddFileRow(ctx, notebook.Id, "docs/sub/deep.md");
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var tree = await svc.GetFolderTreeAsync(project.Id, notebook.Id);

            tree.Should().NotBeNull();
            tree!.Files.Should().ContainSingle(f => f.RelativePath == "rootfile.txt");
            tree.SubFolders.Should().ContainSingle(f => f.Name == "docs");
            var docs = tree.SubFolders.Single(f => f.Name == "docs");
            docs.SubFolders.Should().ContainSingle(f => f.Name == "sub");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFolderTreeAsync_HidesOutputProjectedResourceSymlinkFiles()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            var resourcesDir = Path.Combine(nbRoot, "Resources", "crew-Slide-Shows");
            var outputDir = Path.Combine(nbRoot, "Output");
            Directory.CreateDirectory(resourcesDir);
            Directory.CreateDirectory(outputDir);

            var resourcePath = Path.Combine(resourcesDir, "api.py");
            await File.WriteAllTextAsync(resourcePath, "print('bootstrap')");
            var projectedPath = Path.Combine(outputDir, "api.py");
            var relativeTarget = Path.Combine("..", "Resources", "crew-Slide-Shows", "api.py");
            if (!TryCreateFileSymlink(projectedPath, relativeTarget))
            {
                Assert.Inconclusive("File symlink creation is not available in this environment.");
            }

            var userVisiblePath = Path.Combine(outputDir, "user-visible.txt");
            await File.WriteAllTextAsync(userVisiblePath, "hello");

            AddFileRow(ctx, notebook.Id, "Resources/crew-Slide-Shows/api.py", new FileInfo(resourcePath).Length);
            AddFileRow(ctx, notebook.Id, "Output/api.py", new FileInfo(projectedPath).Length);
            AddFileRow(ctx, notebook.Id, "Output/user-visible.txt", new FileInfo(userVisiblePath).Length);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var tree = await svc.GetFolderTreeAsync(project.Id, notebook.Id);

            tree.Should().NotBeNull();
            var allPaths = EnumerateFilesRecursively(tree!).Select(f => f.RelativePath).ToList();
            allPaths.Should().Contain("Output/user-visible.txt");
            allPaths.Should().NotContain("Output/api.py");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFileAsync_ReturnsNull_WhenDbRecordMissing()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            var result = await svc.GetFileAsync(project.Id, notebook.Id, "missing.txt");

            result.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFileAsync_ReturnsStreamAndContentType_WhenFileExists()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            Directory.CreateDirectory(nbRoot);
            await File.WriteAllTextAsync(Path.Combine(nbRoot, "hello.txt"), "hello world");

            AddFileRow(ctx, notebook.Id, "hello.txt", 11);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var result = await svc.GetFileAsync(project.Id, notebook.Id, "hello.txt");

            result.Should().NotBeNull();
            result!.Value.ContentType.Should().Be("text/plain");
            using var reader = new StreamReader(result.Value.Stream);
            (await reader.ReadToEndAsync()).Should().Be("hello world");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFileContentStreamAsync_ById_ReturnsStream()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            Directory.CreateDirectory(nbRoot);
            await File.WriteAllTextAsync(Path.Combine(nbRoot, "byid.txt"), "by id");

            var fileId = Guid.NewGuid();
            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = fileId,
                NotebookId = notebook.Id,
                RelativePath = "byid.txt",
                FileSize = 5,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var result = await svc.GetFileContentStreamAsync(fileId);

            result.Should().NotBeNull();
            result!.Value.FileName.Should().Be("byid.txt");
            using var reader = new StreamReader(result.Value.Stream);
            (await reader.ReadToEndAsync()).Should().Be("by id");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFileContentStreamAsync_ById_ReturnsNull_WhenFileMissing()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            (await svc.GetFileContentStreamAsync(Guid.NewGuid())).Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateTextFileAsync_WritesFileAndCreatesDbRow()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            var dto = await svc.CreateTextFileAsync(project.Id, notebook.Id, "notes/todo.md", "# Title");

            dto.RelativePath.Should().Be("notes/todo.md");
            var physical = Path.Combine(NotebookRoot(root, project.Id, notebook.Id), "notes", "todo.md");
            File.Exists(physical).Should().BeTrue();
            (await File.ReadAllTextAsync(physical)).Should().Be("# Title");
            ctx.NotebookFiles.Count(f => f.NotebookId == notebook.Id).Should().Be(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateTextFileAsync_Throws_WhenNotebookNotFound()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, _) = await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            var act = async () => await svc.CreateTextFileAsync(project.Id, Guid.NewGuid(), "x.txt", "data");

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Notebook not found*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RenameAsync_RenamesFileAndUpdatesDbRow()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            Directory.CreateDirectory(nbRoot);
            await File.WriteAllTextAsync(Path.Combine(nbRoot, "before.txt"), "data");

            AddFileRow(ctx, notebook.Id, "before.txt");
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var ok = await svc.RenameAsync(project.Id, notebook.Id, "before.txt", "after.txt");

            ok.Should().BeTrue();
            File.Exists(Path.Combine(nbRoot, "after.txt")).Should().BeTrue();
            File.Exists(Path.Combine(nbRoot, "before.txt")).Should().BeFalse();
            ctx.NotebookFiles.Single().RelativePath.Should().Be("after.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MoveAsync_MovesFileToSubfolder()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            Directory.CreateDirectory(nbRoot);
            await File.WriteAllTextAsync(Path.Combine(nbRoot, "movable.txt"), "data");

            AddFileRow(ctx, notebook.Id, "movable.txt");
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var ok = await svc.MoveAsync(project.Id, notebook.Id, "movable.txt", "archive");

            ok.Should().BeTrue();
            File.Exists(Path.Combine(nbRoot, "archive", "movable.txt")).Should().BeTrue();
            ctx.NotebookFiles.Single().RelativePath.Should().Be("archive/movable.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_Throws_ForResourcesFolder()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            var act = async () => await svc.DeleteAsync(project.Id, notebook.Id, "Resources/file.png");

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Resource files cannot be deleted*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RenameAsync_Throws_ForGuideantsFolder()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            var act = async () => await svc.RenameAsync(project.Id, notebook.Id, ".guideants/notebook.json", "x.json");

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Resource files cannot be renamed*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteByIdAsync_DeletesFile()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var nbRoot = NotebookRoot(root, project.Id, notebook.Id);
            Directory.CreateDirectory(nbRoot);
            await File.WriteAllTextAsync(Path.Combine(nbRoot, "del.txt"), "data");

            var fileId = Guid.NewGuid();
            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = fileId,
                NotebookId = notebook.Id,
                RelativePath = "del.txt",
                FileSize = 4,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var ok = await svc.DeleteByIdAsync(project.Id, notebook.Id, fileId);

            ok.Should().BeTrue();
            File.Exists(Path.Combine(nbRoot, "del.txt")).Should().BeFalse();
            ctx.NotebookFiles.Count().Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteByIdAsync_ReturnsFalse_WhenFileMissing()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (project, notebook) = await SeedProjectAndNotebook(ctx);

            var svc = CreateService(ctx, root);
            (await svc.DeleteByIdAsync(project.Id, notebook.Id, Guid.NewGuid())).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetNotebookFile_ReturnsFile_WhenExists()
    {
        var root = CreateTempRoot();
        try
        {
            await using var ctx = CreateContext();
            var (_, notebook) = await SeedProjectAndNotebook(ctx);

            var fileId = Guid.NewGuid();
            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = fileId,
                NotebookId = notebook.Id,
                RelativePath = "f.txt",
                FileSize = 1,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, root);
            var file = await svc.GetNotebookFile(fileId, notebook.Id);

            file.Should().NotBeNull();
            file!.RelativePath.Should().Be("f.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
