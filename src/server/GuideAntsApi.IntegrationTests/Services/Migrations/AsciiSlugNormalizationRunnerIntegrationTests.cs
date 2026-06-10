using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.IntegrationTests.Services.Migrations;

/// <summary>
/// Integration coverage for <see cref="AsciiSlugNormalizationRunner"/>. Like the
/// named-storage runner it opens its own <see cref="ApplicationDbContext"/> from
/// the configured connection string, so these tests point it at the live
/// Testcontainer DB and a per-test temp storage root. They cover the apply path
/// (filesystem move + slug/path DB rewrites), the dry-run path, and the
/// no-changes-required short-circuit.
/// </summary>
[TestClass]
public sealed class AsciiSlugNormalizationRunnerIntegrationTests : BaseEndpointTest
{
    private string _storageRoot = null!;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        _storageRoot = Path.Combine(Path.GetTempPath(), "ascii-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
    }

    [TestCleanup]
    public override Task BaseTestCleanup()
    {
        TryDeleteDirectory(_storageRoot);
        return base.BaseTestCleanup();
    }

    private AsciiSlugNormalizationRunner CreateRunner()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        connectionString.Should().NotBeNullOrWhiteSpace(
            "the integration host must expose the Testcontainer connection string");
        return new AsciiSlugNormalizationRunner(connectionString!, _storageRoot, NullLogger.Instance);
    }

    [TestMethod]
    public async Task RunAsync_Apply_NormalizesUnsafeSlugsAndMovesDirectories()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string projectTitle = "Clean Project Title";
        const string notebookTitle = "Clean Notebook Title";
        const string unsafeProjectSlug = "Bad Slug!";
        const string unsafeNotebookSlug = "Bad NB!";

        var expectedProjectSlug = SlugGenerator.Generate(projectTitle);
        var expectedNotebookSlug = SlugGenerator.Generate(notebookTitle);

        // Project/notebook directory trees named with the unsafe slugs.
        var projectRoot = Path.Combine(_storageRoot, unsafeProjectSlug);
        WriteFile(Path.Combine(projectRoot, "f.txt"), "project file");
        WriteFile(Path.Combine(projectRoot, unsafeNotebookSlug, "n.txt"), "notebook file");

        var casRoot = Path.Combine(_storageRoot, "projects", unsafeProjectSlug);
        WriteFile(Path.Combine(casRoot, "blob.bin"), "cas blob");

        var contentFilePath = Path.Combine(projectRoot, "f.txt");
        var versionStoragePath = Path.Combine(casRoot, "blob.bin");
        await SeedProjectGraphAsync(
            projectId, projectTitle, unsafeProjectSlug,
            notebookId, notebookTitle, unsafeNotebookSlug,
            contentFilePath, versionStoragePath);

        var runner = CreateRunner();
        var result = await runner.RunAsync(apply: true);
        result.Should().Be(0);

        // Filesystem: unsafe directories renamed to normalized slugs.
        Directory.Exists(projectRoot).Should().BeFalse();
        Directory.Exists(Path.Combine(_storageRoot, expectedProjectSlug)).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, expectedProjectSlug, "f.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, expectedProjectSlug, expectedNotebookSlug, "n.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(_storageRoot, "projects", expectedProjectSlug)).Should().BeTrue();

        // DB: slug columns + rewritten paths committed.
        await AssertProjectSlugAsync(projectId, expectedProjectSlug);
        await AssertNotebookSlugAsync(notebookId, expectedNotebookSlug);
        await AssertContentFilePathAsync(projectId, Path.Combine(_storageRoot, expectedProjectSlug, "f.txt"));
        await AssertVersionStoragePathAsync(projectId, Path.Combine(_storageRoot, "projects", expectedProjectSlug, "blob.bin"));
    }

    [TestMethod]
    public async Task RunAsync_DryRun_DoesNotMoveDirectoriesOrChangeSlugs()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string unsafeProjectSlug = "Bad Slug!";
        const string unsafeNotebookSlug = "Bad NB!";

        var projectRoot = Path.Combine(_storageRoot, unsafeProjectSlug);
        WriteFile(Path.Combine(projectRoot, "f.txt"), "project file");

        await SeedProjectGraphAsync(
            projectId, "Clean Project Title", unsafeProjectSlug,
            notebookId, "Clean Notebook Title", unsafeNotebookSlug,
            Path.Combine(projectRoot, "f.txt"), storagePath: null);

        var runner = CreateRunner();
        var result = await runner.RunAsync(apply: false);
        result.Should().Be(0);

        Directory.Exists(projectRoot).Should().BeTrue();
        Directory.Exists(Path.Combine(_storageRoot, SlugGenerator.Generate("Clean Project Title"))).Should().BeFalse();
        await AssertProjectSlugAsync(projectId, unsafeProjectSlug);
    }

    [TestMethod]
    public async Task RunAsync_Apply_AllSlugsSafe_NoChangesRequired()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string safeProjectSlug = "already-safe-project";
        const string safeNotebookSlug = "already-safe-notebook";

        WriteFile(Path.Combine(_storageRoot, safeProjectSlug, "f.txt"), "project file");

        await SeedProjectGraphAsync(
            projectId, "Already Safe Project", safeProjectSlug,
            notebookId, "Already Safe Notebook", safeNotebookSlug,
            Path.Combine(_storageRoot, safeProjectSlug, "f.txt"), storagePath: null);

        var runner = CreateRunner();
        var result = await runner.RunAsync(apply: true);
        result.Should().Be(0);

        // No slug changes -> directory untouched and slug preserved.
        Directory.Exists(Path.Combine(_storageRoot, safeProjectSlug)).Should().BeTrue();
        await AssertProjectSlugAsync(projectId, safeProjectSlug);
    }

    private async Task SeedProjectGraphAsync(
        Guid projectId,
        string projectTitle,
        string projectSlug,
        Guid notebookId,
        string notebookTitle,
        string notebookSlug,
        string contentFilePath,
        string? storagePath)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Projects.Add(new Project
        {
            Id = projectId,
            Title = projectTitle,
            Slug = projectSlug,
            Created = DateTime.UtcNow
        });

        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            Title = notebookTitle,
            Slug = notebookSlug,
            ProjectId = projectId,
            Created = DateTime.UtcNow
        });

        var contentFile = new ContentFile
        {
            Id = Guid.NewGuid(),
            FileName = "f.txt",
            Path = contentFilePath,
            RelativePath = "f.txt",
            FileSize = 12,
            ContentType = "text/plain",
            ProjectId = projectId,
            Created = DateTime.UtcNow
        };
        contentFile.GenerateDocumentId();
        db.ContentFiles.Add(contentFile);

        db.ContentFileVersions.Add(new ContentFileVersion
        {
            Id = Guid.NewGuid(),
            ContentFileId = contentFile.Id,
            VersionNumber = 1,
            FileName = "f.txt",
            Path = contentFilePath,
            StoragePath = storagePath,
            RelativePath = "f.txt",
            FileSize = 12,
            ContentType = "text/plain",
            Indexed = false,
            Created = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private async Task AssertProjectSlugAsync(Guid projectId, string expectedSlug)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var slug = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.Slug).SingleAsync();
        slug.Should().Be(expectedSlug);
    }

    private async Task AssertNotebookSlugAsync(Guid notebookId, string expectedSlug)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var slug = await db.Notebooks.AsNoTracking()
            .Where(n => n.Id == notebookId).Select(n => n.Slug).SingleAsync();
        slug.Should().Be(expectedSlug);
    }

    private async Task AssertContentFilePathAsync(Guid projectId, string expectedPath)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var path = await db.ContentFiles.AsNoTracking()
            .Where(c => c.ProjectId == projectId).Select(c => c.Path).SingleAsync();
        path.Should().Be(expectedPath);
    }

    private async Task AssertVersionStoragePathAsync(Guid projectId, string expectedStoragePath)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storagePath = await db.ContentFileVersions.AsNoTracking()
            .Where(v => v.ContentFile.ProjectId == projectId).Select(v => v.StoragePath).SingleAsync();
        storagePath.Should().Be(expectedStoragePath);
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the per-test temp storage root.
        }
    }
}
