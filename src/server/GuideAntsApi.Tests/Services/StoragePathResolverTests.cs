using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class StoragePathResolverTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
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

    private static StoragePathResolver CreateResolver(ApplicationDbContext context, string storageRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = storageRoot })
            .Build();

        return new StoragePathResolver(
            CreateScopeFactory(context),
            configuration,
            NullLogger<StoragePathResolver>.Instance);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "spr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [TestMethod]
    public void Constructor_Throws_WhenFileStoragePathMissing()
    {
        using var ctx = CreateContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        Action act = () => new StoragePathResolver(
            CreateScopeFactory(ctx),
            configuration,
            NullLogger<StoragePathResolver>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FileStorage:Path is not configured*");
    }

    [TestMethod]
    public void GetStorageRoot_ReturnsConfiguredRoot()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var resolver = CreateResolver(ctx, root);
            resolver.GetStorageRoot().Should().Be(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetProjectRootPath_UsesProjectSlug_AndCreatesDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "My Project", Slug = "my-project" };
            ctx.Projects.Add(project);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var path = resolver.GetProjectRootPath(project.Id);

            path.Should().Be(Path.Combine(root, "my-project"));
            Directory.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetProjectRootPath_GeneratesSlug_WhenProjectMissing()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var resolver = CreateResolver(ctx, root);
            var missingId = Guid.NewGuid();

            // No project row exists -> slug is generated from the id string.
            var path = resolver.GetProjectRootPath(missingId);

            Directory.Exists(path).Should().BeTrue();
            Path.GetDirectoryName(path).Should().Be(root.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetNotebookRootPath_CreatesDirectory_AndWritesAssociationMetadata()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "p" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", Slug = "nb" };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var path = resolver.GetNotebookRootPath(project.Id, notebook.Id);

            path.Should().Be(Path.Combine(root, "p", "nb"));
            Directory.Exists(path).Should().BeTrue();

            var metadataPath = Path.Combine(path, ".guideants", "notebook.json");
            File.Exists(metadataPath).Should().BeTrue();
            File.ReadAllText(metadataPath).Should().Contain(notebook.Id.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetNotebookRootPath_ResolvesExternallyRenamedFolder_ViaMetadata()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var projectId = Guid.NewGuid();
            var notebookId = Guid.NewGuid();
            var project = new Project { Id = projectId, Title = "P", Slug = "p" };
            // Notebook slug 'expected-nb' will NOT exist on disk; a renamed folder will.
            var notebook = new Notebook { Id = notebookId, ProjectId = projectId, Title = "NB", Slug = "expected-nb" };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            ctx.SaveChanges();

            var projectRoot = Path.Combine(root, "p");
            var renamedFolder = Path.Combine(projectRoot, "human-renamed");
            var metaDir = Path.Combine(renamedFolder, ".guideants");
            Directory.CreateDirectory(metaDir);
            File.WriteAllText(
                Path.Combine(metaDir, "notebook.json"),
                JsonSerializer.Serialize(new { SchemaVersion = 1, ProjectId = projectId, NotebookId = notebookId }));

            var resolver = CreateResolver(ctx, root);
            var path = resolver.GetNotebookRootPath(projectId, notebookId);

            path.Should().Be(renamedFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetContainerNotebookRootPath_BuildsContainerStylePath()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "alpha" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", Slug = "beta" };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var path = resolver.GetContainerNotebookRootPath(project.Id, notebook.Id);

            path.Should().Be("/app/ContentFiles/alpha/beta");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetContentAddressablePath_SplitsHashIntoSubdirectories()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "proj" };
            ctx.Projects.Add(project);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var hash = "abcdef1234567890";
            var path = resolver.GetContentAddressablePath(project.Id, hash);

            path.Should().Be(Path.Combine(root, "projects", "proj", "content", "ab", "cd", hash));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetProjectMarkdownShadowPath_AppendsMarkdownExtension()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "proj" };
            ctx.Projects.Add(project);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var hash = "abcdef1234567890";
            var path = resolver.GetProjectMarkdownShadowPath(project.Id, hash);

            path.Should().Be(Path.Combine(root, "projects", "proj", "content", "ab", "cd", hash + ".md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void GetNotebookMarkdownShadowPath_BuildsShadowPathUnderNotebook()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "proj" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", Slug = "nb" };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var hash = "abcdef1234567890";
            var path = resolver.GetNotebookMarkdownShadowPath(project.Id, notebook.Id, hash);

            path.Should().Be(Path.Combine(root, "projects", "proj", "nb", "markdown", "ab", "cd", hash + ".md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InvalidateProject_ForcesSlugReread_AfterChange()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "old-slug" };
            ctx.Projects.Add(project);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            var first = resolver.GetProjectRootPath(project.Id);
            first.Should().Be(Path.Combine(root, "old-slug"));

            // Mutate slug and invalidate the cache; resolver must re-read from db.
            project.Slug = "new-slug";
            ctx.SaveChanges();
            resolver.InvalidateProject(project.Id);

            var second = resolver.GetProjectRootPath(project.Id);
            second.Should().Be(Path.Combine(root, "new-slug"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InvalidateNotebook_ForcesSlugReread_AfterChange()
    {
        var root = CreateTempRoot();
        try
        {
            using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "p" };
            var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "NB", Slug = "nb-old" };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);
            ctx.SaveChanges();

            var resolver = CreateResolver(ctx, root);
            // Use the container path which is computed purely from the slug (no on-disk folder
            // discovery), so we can observe the slug-cache invalidation deterministically.
            resolver.GetContainerNotebookRootPath(project.Id, notebook.Id).Should().Be("/app/ContentFiles/p/nb-old");

            // Without invalidation the resolver keeps returning the cached slug.
            notebook.Slug = "nb-new";
            ctx.SaveChanges();
            resolver.GetContainerNotebookRootPath(project.Id, notebook.Id).Should().Be("/app/ContentFiles/p/nb-old");

            // After invalidation it re-reads the new slug from the database.
            resolver.InvalidateNotebook(notebook.Id);
            resolver.GetContainerNotebookRootPath(project.Id, notebook.Id).Should().Be("/app/ContentFiles/p/nb-new");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
