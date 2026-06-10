using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Core;

[TestClass]
public sealed class ProjectServiceTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static IConfiguration CreateConfig(string storagePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = storagePath })
            .Build();

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

    private static ProjectService CreateService(ApplicationDbContext ctx, string storage) =>
        new(CreateScopeFactory(ctx), CreateConfig(storage), NullLogger<ProjectService>.Instance);

    private static ProjectService CreateService(ApplicationDbContext ctx, string storage, IStoragePathResolver resolver) =>
        new(CreateScopeFactory(ctx), CreateConfig(storage), resolver, NullLogger<ProjectService>.Instance);

    private static string NewTmpDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Resolver that derives the project root from the live (tracked) project slug,
    /// reproducing the slug-based folder layout the real resolver uses so the
    /// directory-rename branch in UpdateProjectAsync can be exercised.
    /// </summary>
    private sealed class SlugPathResolver(ApplicationDbContext ctx, string root) : IStoragePathResolver
    {
        public string GetStorageRoot() => root;
        public void InvalidateProject(Guid projectId) { }
        public void InvalidateNotebook(Guid notebookId) { }

        public string GetProjectRootPath(Guid projectId)
        {
            var slug = ctx.Projects.Find(projectId)?.Slug;
            return Path.Combine(root, string.IsNullOrEmpty(slug) ? projectId.ToString() : slug);
        }

        public string GetNotebookRootPath(Guid projectId, Guid notebookId) =>
            Path.Combine(GetProjectRootPath(projectId), "notebooks", notebookId.ToString());

        public string GetContainerNotebookRootPath(Guid projectId, Guid notebookId) =>
            GetNotebookRootPath(projectId, notebookId);

        public string GetContentAddressablePath(Guid projectId, string contentHash) =>
            Path.Combine(root, contentHash);

        public string GetProjectMarkdownShadowPath(Guid projectId, string contentHash) =>
            Path.Combine(root, contentHash + ".md");

        public string GetNotebookMarkdownShadowPath(Guid projectId, Guid notebookId, string contentHash) =>
            Path.Combine(root, contentHash + ".md");
    }

    [TestMethod]
    public async Task CreateProjectAsync_GeneratesUniqueSlug_WhenTitleCollides()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var existingSlug = SlugGenerator.Generate("Shared Title");
            ctx.Projects.Add(new Project { Id = Guid.NewGuid(), Title = "Shared Title", Slug = existingSlug });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, storage);
            var created = await svc.CreateProjectAsync(new CreateProjectDto("Shared Title", "Desc"));

            created.Title.Should().Be("Shared Title");
            var slugs = ctx.Projects.Select(p => p.Slug).ToList();
            slugs.Should().OnlyHaveUniqueItems();
            slugs.Should().Contain(SlugGenerator.AddNumericSuffix(existingSlug, 2));
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task UpdateProjectAsync_RenamesProjectDirectory_WhenSlugChanges()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "Old Title", Slug = "old-title", Description = "D" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            var oldRoot = Path.Combine(storage, "old-title");
            Directory.CreateDirectory(oldRoot);
            await File.WriteAllTextAsync(Path.Combine(oldRoot, "marker.txt"), "x");

            var svc = CreateService(ctx, storage, new SlugPathResolver(ctx, storage));
            var result = await svc.UpdateProjectAsync(project.Id, new UpdateProjectDto("New Title", "D2"));

            result.Should().NotBeNull();
            result!.Title.Should().Be("New Title");

            var newRoot = Path.Combine(storage, "new-title");
            Directory.Exists(newRoot).Should().BeTrue();
            Directory.Exists(oldRoot).Should().BeFalse();
            File.Exists(Path.Combine(newRoot, "marker.txt")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task UpdateProjectAsync_Throws_WhenTargetDirectoryAlreadyExists()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "Old Title", Slug = "old-title", Description = "D" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            Directory.CreateDirectory(Path.Combine(storage, "old-title"));
            Directory.CreateDirectory(Path.Combine(storage, "new-title"));

            var svc = CreateService(ctx, storage, new SlugPathResolver(ctx, storage));

            var act = async () => await svc.UpdateProjectAsync(project.Id, new UpdateProjectDto("New Title", null));

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task UpdateProjectAsync_UpdatesDescriptionOnly_WhenTitleBlank()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "Keep", Slug = "keep", Description = "Old" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, storage);
            var result = await svc.UpdateProjectAsync(project.Id, new UpdateProjectDto("", "New Desc"));

            result.Should().NotBeNull();
            result!.Title.Should().Be("Keep");
            (await ctx.Projects.FindAsync(project.Id))!.Description.Should().Be("New Desc");
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task DeleteProjectAsync_RemovesProjectRootDirectory()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "p", Description = "D" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            var root = Path.Combine(storage, "p");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "f.txt"), "x");

            var svc = CreateService(ctx, storage, new SlugPathResolver(ctx, storage));
            var ok = await svc.DeleteProjectAsync(project.Id);

            ok.Should().BeTrue();
            Directory.Exists(root).Should().BeFalse();
            (await ctx.Projects.FindAsync(project.Id)).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task SetHomePageAsync_SetsHomePage_WhenProjectExists()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "p" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, storage);
            var fileId = Guid.NewGuid();
            var ok = await svc.SetHomePageAsync(project.Id, fileId);

            ok.Should().BeTrue();
            (await ctx.Projects.FindAsync(project.Id))!.HomePageContentFileId.Should().Be(fileId);
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task SetHomePageAsync_ReturnsFalse_WhenProjectMissing()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var svc = CreateService(ctx, storage);

            (await svc.SetHomePageAsync(Guid.NewGuid(), Guid.NewGuid())).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task ClearHomePageAsync_ClearsHomePage_WhenProjectExists()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P", Slug = "p", HomePageContentFileId = Guid.NewGuid() };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, storage);
            var ok = await svc.ClearHomePageAsync(project.Id);

            ok.Should().BeTrue();
            (await ctx.Projects.FindAsync(project.Id))!.HomePageContentFileId.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task ClearHomePageAsync_ReturnsFalse_WhenProjectMissing()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var svc = CreateService(ctx, storage);

            (await svc.ClearHomePageAsync(Guid.NewGuid())).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task GetProjectAsync_ReturnsProject_WhenNotDeleted()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "Live", Slug = "live" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, storage);
            var result = await svc.GetProjectAsync(project.Id);

            result.Should().NotBeNull();
            result!.Title.Should().Be("Live");
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task GetProjectsAsync_OrdersByLastActivityDescending()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var older = new Project { Id = Guid.NewGuid(), Title = "Older", Slug = "older" };
            var newer = new Project { Id = Guid.NewGuid(), Title = "Newer", Slug = "newer" };
            ctx.Projects.AddRange(older, newer);

            ctx.UsageEvents.Add(new UsageEvent { ProjectId = older.Id, Created = DateTime.UtcNow.AddDays(-5) });
            ctx.UsageEvents.Add(new UsageEvent { ProjectId = newer.Id, Created = DateTime.UtcNow.AddMinutes(-1) });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, storage);
            var result = (await svc.GetProjectsAsync()).ToList();

            result.Should().HaveCount(2);
            result[0].Title.Should().Be("Newer");
            result[1].Title.Should().Be("Older");
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }

    [TestMethod]
    public async Task GetProjectsAsync_ReturnsEmpty_WhenNoProjects()
    {
        var storage = NewTmpDir();
        try
        {
            await using var ctx = CreateContext();
            var svc = CreateService(ctx, storage);

            (await svc.GetProjectsAsync()).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, true);
        }
    }
}
