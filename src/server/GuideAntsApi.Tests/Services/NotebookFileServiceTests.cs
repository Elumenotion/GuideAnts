using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAnts.Usage;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class NotebookFileServiceTests
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
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    private static IFileLineageService CreateLineageMock() => Mock.Of<IFileLineageService>();

    private static IContentFileService CreateContentFileServiceMock() => Mock.Of<IContentFileService>();

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

    private static NotebookFileService CreateService(
        ApplicationDbContext ctx,
        string storagePath,
        IMarkdownExtractionService? markdownExtractionService = null)
    {
        var scopeFactory = CreateScopeFactory(ctx);
        var config = CreateConfig(storagePath);
        markdownExtractionService ??= Mock.Of<IMarkdownExtractionService>();

        var sync = new NotebookFileSyncService(
            scopeFactory,
            config,
            NullLogger<NotebookFileSyncService>.Instance,
            CreateLineageMock(),
            markdownExtractionService,
            Mock.Of<IUsageRecorder>(),
            Mock.Of<INotebookLockService>());

        return new NotebookFileService(
            scopeFactory,
            config,
            sync,
            NullLogger<NotebookFileService>.Instance,
            CreateLineageMock(),
            CreateContentFileServiceMock(),
            markdownExtractionService);
    }

    [TestMethod]
    public async Task UploadFilesAsync_CreatesDbRowAndPhysicalFile()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);

            var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
            var formFile = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "test.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            };

            var result = await svc.UploadFilesAsync(project.Id, notebook.Id, new FormFileCollection { formFile }, "");

            Assert.AreEqual(1, result.Count());
            Assert.AreEqual(1, ctx.NotebookFiles.Count());

            var physical = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString(), "test.txt");
            Assert.IsTrue(File.Exists(physical));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task CreateFolderAsync_CreatesDirectory()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var tree = await svc.CreateFolderAsync(project.Id, notebook.Id, "docs/sub");

            Assert.IsNotNull(tree);
            var folderPath = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString(), "docs", "sub");
            Assert.IsTrue(Directory.Exists(folderPath));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesFileAndDbRow()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);

            var filePath = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString(), "delete-me.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "to delete");

            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = Guid.NewGuid(),
                NotebookId = notebook.Id,
                RelativePath = "delete-me.txt",
                FileSize = new FileInfo(filePath).Length,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "abc",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var ok = await svc.DeleteAsync(project.Id, notebook.Id, "delete-me.txt");

            Assert.IsTrue(ok);
            Assert.IsFalse(File.Exists(filePath));
            Assert.AreEqual(0, ctx.NotebookFiles.Count());
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task UploadFilesAsync_WhenExistingFileContentChanges_RequeuesMarkdownExtraction()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var markdown = new Mock<IMarkdownExtractionService>();
            markdown
                .Setup(m => m.CreateNotebookMarkdownShadowAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid notebookFileId, CancellationToken _) => new NotebookFileMarkdownShadow
                {
                    Id = Guid.NewGuid(),
                    OriginalNotebookFileId = notebookFileId,
                    Status = MarkdownExtractionStatus.Pending
                });
            markdown
                .Setup(m => m.RetryNotebookExtractionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid notebookFileId, CancellationToken _) => new NotebookFileMarkdownShadow
                {
                    Id = Guid.NewGuid(),
                    OriginalNotebookFileId = notebookFileId,
                    Status = MarkdownExtractionStatus.Pending
                });

            var svc = CreateService(ctx, tmpDir, markdown.Object);

            var firstBytes = System.Text.Encoding.UTF8.GetBytes("first");
            var firstFile = new FormFile(new MemoryStream(firstBytes), 0, firstBytes.Length, "file", "deck.pptx")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            };

            await svc.UploadFilesAsync(project.Id, notebook.Id, new FormFileCollection { firstFile }, "");

            var notebookFileId = ctx.NotebookFiles.Single().Id;
            var secondBytes = System.Text.Encoding.UTF8.GetBytes("second");
            var secondFile = new FormFile(new MemoryStream(secondBytes), 0, secondBytes.Length, "file", "deck.pptx")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            };

            await svc.UploadFilesAsync(project.Id, notebook.Id, new FormFileCollection { secondFile }, "");

            markdown.Verify(m => m.CreateNotebookMarkdownShadowAsync(notebookFileId, It.IsAny<CancellationToken>()), Times.Once);
            markdown.Verify(m => m.RetryNotebookExtractionAsync(notebookFileId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task UploadFilesAsync_SanitizesFileName_AndKeepsWriteUnderNotebookRoot()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
            var formFile = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "../../evil.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            };

            var result = await svc.UploadFilesAsync(project.Id, notebook.Id, new FormFileCollection { formFile }, "uploads");

            Assert.AreEqual(1, result.Count());
            var dbFile = ctx.NotebookFiles.Single();
            Assert.AreEqual("uploads/evil.txt", dbFile.RelativePath);

            var expected = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString(), "uploads", "evil.txt");
            Assert.IsTrue(File.Exists(expected));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task GetFileContentStreamAsync_AllowsLlmRelativePathResolution()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var notebookRoot = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString());
            var resourcesPath = Path.Combine(notebookRoot, "Resources");
            Directory.CreateDirectory(resourcesPath);
            var filePath = Path.Combine(resourcesPath, "image.png");
            await File.WriteAllTextAsync(filePath, "hello");

            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = Guid.NewGuid(),
                NotebookId = notebook.Id,
                RelativePath = "Resources/image.png",
                FileSize = new FileInfo(filePath).Length,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "abc",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var result = await svc.GetFileContentStreamAsync(project.Id, notebook.Id, "../Resources/image.png");

            Assert.AreEqual("image/png", result.contentType);
            using var reader = new StreamReader(result.stream);
            var content = await reader.ReadToEndAsync();
            Assert.AreEqual("hello", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task CreateFolderAsync_ReturnsNull_WhenPathEscapesNotebookRoot()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var result = await svc.CreateFolderAsync(project.Id, notebook.Id, "../../outside");

            Assert.IsNull(result);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_ReturnsFalse_WhenPathEscapesNotebookRoot()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var result = await svc.DeleteAsync(project.Id, notebook.Id, "../../outside.txt");

            Assert.IsFalse(result);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task RenameAsync_ReturnsFalse_ForInvalidNewName()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var notebookRoot = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString());
            Directory.CreateDirectory(notebookRoot);
            var sourcePath = Path.Combine(notebookRoot, "source.txt");
            await File.WriteAllTextAsync(sourcePath, "hello");

            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = Guid.NewGuid(),
                NotebookId = notebook.Id,
                RelativePath = "source.txt",
                FileSize = new FileInfo(sourcePath).Length,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "abc",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var result = await svc.RenameAsync(project.Id, notebook.Id, "source.txt", "..");

            Assert.IsFalse(result);
            Assert.IsTrue(File.Exists(sourcePath));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task MoveAsync_ReturnsFalse_WhenDestinationEscapesNotebookRoot()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", NotebookTemplateId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            await ctx.SaveChangesAsync();

            var notebookRoot = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString());
            Directory.CreateDirectory(notebookRoot);
            var sourcePath = Path.Combine(notebookRoot, "source.txt");
            await File.WriteAllTextAsync(sourcePath, "hello");

            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = Guid.NewGuid(),
                NotebookId = notebook.Id,
                RelativePath = "source.txt",
                FileSize = new FileInfo(sourcePath).Length,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "abc",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var result = await svc.MoveAsync(project.Id, notebook.Id, "source.txt", "../../outside");

            Assert.IsFalse(result);
            Assert.IsTrue(File.Exists(sourcePath));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }
}
