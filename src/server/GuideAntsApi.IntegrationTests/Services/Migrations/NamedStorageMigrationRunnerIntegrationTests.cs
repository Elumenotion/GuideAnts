using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.IntegrationTests.Services.Migrations;

/// <summary>
/// Integration coverage for <see cref="NamedStorageMigrationRunner"/>. The runner
/// constructs its own <see cref="ApplicationDbContext"/> via the configured
/// connection string, so these tests resolve the live Testcontainer connection
/// string from the integration host configuration and seed both real filesystem
/// directories (under a per-test temp root) and real DB rows. Each test asserts
/// the on-disk moves and the rewritten DB paths produced by the runner.
/// </summary>
[TestClass]
public sealed class NamedStorageMigrationRunnerIntegrationTests : BaseEndpointTest
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
        _storageRoot = Path.Combine(Path.GetTempPath(), "nsm-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
    }

    [TestCleanup]
    public override Task BaseTestCleanup()
    {
        TryDeleteDirectory(_storageRoot);
        return base.BaseTestCleanup();
    }

    private NamedStorageMigrationRunner CreateRunner()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        connectionString.Should().NotBeNullOrWhiteSpace(
            "the integration host must expose the Testcontainer connection string");
        return new NamedStorageMigrationRunner(connectionString!, _storageRoot, NullLogger.Instance);
    }

    [TestMethod]
    public async Task RunAsync_Apply_MovesGuidDirectoriesToSlugsAndRewritesDbPaths()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string projectSlug = "alpha-project";
        const string notebookSlug = "alpha-notes";

        // Filesystem: legacy GUID-named project + notebook trees, plus CAS mirror.
        var legacyProjectRoot = Path.Combine(_storageRoot, projectId.ToString());
        WriteFile(Path.Combine(legacyProjectRoot, "doc.md"), "project file");
        WriteFile(Path.Combine(legacyProjectRoot, "notebooks", notebookId.ToString(), "page.md"), "notebook file");

        var legacyCasRoot = Path.Combine(_storageRoot, "projects", projectId.ToString());
        WriteFile(Path.Combine(legacyCasRoot, "blob.bin"), "cas blob");
        WriteFile(Path.Combine(legacyCasRoot, "notebooks", notebookId.ToString(), "casblob.bin"), "cas nb blob");

        var contentFilePath = Path.Combine(legacyProjectRoot, "doc.md");
        var versionStoragePath = Path.Combine(legacyCasRoot, "blob.bin");
        await SeedProjectGraphAsync(projectId, projectSlug, notebookId, notebookSlug, contentFilePath, versionStoragePath);

        var runner = CreateRunner();
        var result = await runner.RunAsync(apply: true);
        result.Should().Be(0);

        // Filesystem: GUID directories are gone; slug directories exist with content.
        Directory.Exists(legacyProjectRoot).Should().BeFalse();
        Directory.Exists(Path.Combine(_storageRoot, projectSlug)).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, projectSlug, "doc.md")).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, projectSlug, notebookSlug, "page.md")).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, projectSlug, notebookSlug, ".guideants", "notebook.json")).Should().BeTrue();

        Directory.Exists(legacyCasRoot).Should().BeFalse();
        Directory.Exists(Path.Combine(_storageRoot, "projects", projectSlug)).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, "projects", projectSlug, "blob.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_storageRoot, "projects", projectSlug, notebookSlug, "casblob.bin")).Should().BeTrue();

        // DB rewrites are committed.
        await AssertContentFilePathAsync(projectId, Path.Combine(_storageRoot, projectSlug, "doc.md"));
        await AssertVersionStoragePathAsync(projectId, Path.Combine(_storageRoot, "projects", projectSlug, "blob.bin"));
    }

    [TestMethod]
    public async Task RunAsync_DryRun_DoesNotMoveDirectoriesOrChangeDb()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string projectSlug = "beta-project";
        const string notebookSlug = "beta-notes";

        var legacyProjectRoot = Path.Combine(_storageRoot, projectId.ToString());
        WriteFile(Path.Combine(legacyProjectRoot, "doc.md"), "project file");

        var contentFilePath = Path.Combine(legacyProjectRoot, "doc.md");
        await SeedProjectGraphAsync(projectId, projectSlug, notebookId, notebookSlug, contentFilePath, storagePath: null);

        var runner = CreateRunner();
        var result = await runner.RunAsync(apply: false);
        result.Should().Be(0);

        // Dry-run rolls back the transaction and performs no filesystem moves.
        Directory.Exists(legacyProjectRoot).Should().BeTrue();
        Directory.Exists(Path.Combine(_storageRoot, projectSlug)).Should().BeFalse();
        await AssertContentFilePathAsync(projectId, contentFilePath);
    }

    [TestMethod]
    public async Task RunAsync_Apply_ArchivesUnmappedLegacyGuidDirectories()
    {
        // Known project so the runner has at least one mapped project to process.
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string projectSlug = "gamma-project";
        const string notebookSlug = "gamma-notes";
        WriteFile(Path.Combine(_storageRoot, projectId.ToString(), "doc.md"), "project file");
        await SeedProjectGraphAsync(projectId, projectSlug, notebookId, notebookSlug,
            Path.Combine(_storageRoot, projectId.ToString(), "doc.md"), storagePath: null);

        // Orphan GUID directories that map to no known project must be archived.
        var orphanRootGuid = Guid.NewGuid().ToString();
        WriteFile(Path.Combine(_storageRoot, orphanRootGuid, "stray.md"), "orphan root");
        var orphanCasGuid = Guid.NewGuid().ToString();
        WriteFile(Path.Combine(_storageRoot, "projects", orphanCasGuid, "stray.bin"), "orphan cas");

        var runner = CreateRunner();
        var result = await runner.RunAsync(apply: true);
        result.Should().Be(0);

        Directory.Exists(Path.Combine(_storageRoot, orphanRootGuid)).Should().BeFalse();
        Directory.Exists(Path.Combine(_storageRoot, "_legacy-unmapped", "project-roots", orphanRootGuid))
            .Should().BeTrue();

        Directory.Exists(Path.Combine(_storageRoot, "projects", orphanCasGuid)).Should().BeFalse();
        Directory.Exists(Path.Combine(_storageRoot, "_legacy-unmapped", "projects-cas", orphanCasGuid))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task RunAsync_Apply_TargetSlugDirectoryAlreadyExists_ThrowsAndRollsBack()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string projectSlug = "delta-project";
        const string notebookSlug = "delta-notes";

        var legacyProjectRoot = Path.Combine(_storageRoot, projectId.ToString());
        WriteFile(Path.Combine(legacyProjectRoot, "doc.md"), "project file");
        // Pre-create the destination slug directory to force the move conflict.
        Directory.CreateDirectory(Path.Combine(_storageRoot, projectSlug));

        await SeedProjectGraphAsync(projectId, projectSlug, notebookId, notebookSlug,
            Path.Combine(legacyProjectRoot, "doc.md"), storagePath: null);

        var runner = CreateRunner();
        Func<Task> act = () => runner.RunAsync(apply: true);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The source GUID directory remains because the move was rejected.
        Directory.Exists(legacyProjectRoot).Should().BeTrue();
    }

    private async Task SeedProjectGraphAsync(
        Guid projectId,
        string projectSlug,
        Guid notebookId,
        string notebookSlug,
        string contentFilePath,
        string? storagePath)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var project = new Project
        {
            Id = projectId,
            Title = "Project " + projectSlug,
            Slug = projectSlug,
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            Title = "Notebook " + notebookSlug,
            Slug = notebookSlug,
            ProjectId = projectId,
            Created = DateTime.UtcNow
        });

        var contentFile = new ContentFile
        {
            Id = Guid.NewGuid(),
            FileName = "doc.md",
            Path = contentFilePath,
            RelativePath = "doc.md",
            FileSize = 11,
            ContentType = "text/markdown",
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
            FileName = "doc.md",
            Path = contentFilePath,
            StoragePath = storagePath,
            RelativePath = "doc.md",
            FileSize = 11,
            ContentType = "text/markdown",
            Indexed = false,
            Created = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private async Task AssertContentFilePathAsync(Guid projectId, string expectedPath)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var path = await db.ContentFiles
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .Select(c => c.Path)
            .SingleAsync();
        path.Should().Be(expectedPath);
    }

    private async Task AssertVersionStoragePathAsync(Guid projectId, string expectedStoragePath)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storagePath = await db.ContentFileVersions
            .AsNoTracking()
            .Where(v => v.ContentFile.ProjectId == projectId)
            .Select(v => v.StoragePath)
            .SingleAsync();
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
