using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostFolderMountLeafValidatorTests
{
    private readonly HostFolderMountLeafValidator _validator = new();

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void ValidateFormat_RejectsEmpty(string leafName)
    {
        var result = _validator.ValidateFormat(leafName);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_empty");
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    public void ValidateFormat_RejectsDotSegments(string leafName)
    {
        var result = _validator.ValidateFormat(leafName);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_reserved_dot");
    }

    [TestMethod]
    [DataRow("a/b")]
    [DataRow(@"a\b")]
    public void ValidateFormat_RejectsPathSeparators(string leafName)
    {
        var result = _validator.ValidateFormat(leafName);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_path_separator");
    }

    [TestMethod]
    public void ValidateFormat_RejectsNullCharacter()
    {
        var result = _validator.ValidateFormat("bad\0name");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_null_char");
    }

    [TestMethod]
    [DataRow(".guideants")]
    [DataRow("Output")]
    [DataRow("Runs")]
    [DataRow("Resources")]
    [DataRow("files")]
    [DataRow("FILES")]
    public void ValidateFormat_RejectsReservedNames(string leafName)
    {
        var result = _validator.ValidateFormat(leafName);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_reserved_name");
    }

    [TestMethod]
    public void ValidateFormat_AcceptsNormalLeaf()
    {
        _validator.ValidateFormat("Shared Reports").IsValid.Should().BeTrue();
    }

    [TestMethod]
    public async Task ValidateCollisionsAsync_RejectsFilesystemCollision()
    {
        var storageRoot = CreateTempDirectory();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);
        Directory.CreateDirectory(Path.Combine(notebookRoot, "Existing"));

        await using var db = CreateContext();
        var pathResolver = CreatePathResolver(storageRoot, projectId, notebookId, notebookRoot);

        var result = await _validator.ValidateCollisionsAsync(
            db,
            pathResolver.Object,
            projectId,
            [notebookId],
            "Existing");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_filesystem_collision");
    }

    [TestMethod]
    public async Task ValidateCollisionsAsync_RejectsActiveMappingCollision()
    {
        var storageRoot = CreateTempDirectory();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);

        await using var db = CreateContext();
        var existingMountId = Guid.NewGuid();
        db.HostFolderMounts.Add(new HostFolderMount
        {
            Id = existingMountId,
            ProjectId = projectId,
            Scope = HostFolderMountScope.Notebook,
            NotebookId = notebookId,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "Existing",
            LeafName = "Shared",
            MountKey = HostFolderMountKeyDeriver.DeriveMountKey(existingMountId),
            SourceSpec = @"D:\Data\Existing",
            ContainerSourcePath = "/app/HostMounts/existing",
            CreatedByUserId = Guid.NewGuid(),
            Links =
            [
                new HostFolderMountLink
                {
                    NotebookId = notebookId,
                    LinkRelativePath = "Shared",
                    LinkPhysicalPath = Path.Combine(notebookRoot, "Shared"),
                    Status = HostFolderMountLinkStatus.Linked
                }
            ]
        });
        await db.SaveChangesAsync();

        var pathResolver = CreatePathResolver(storageRoot, projectId, notebookId, notebookRoot);

        var result = await _validator.ValidateCollisionsAsync(
            db,
            pathResolver.Object,
            projectId,
            [notebookId],
            "Shared");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_active_mapping_collision");
    }

    [TestMethod]
    public async Task ValidateCollisionsAsync_ProjectScopeChecksEveryNotebook()
    {
        var storageRoot = CreateTempDirectory();
        var projectId = Guid.NewGuid();
        var notebookA = Guid.NewGuid();
        var notebookB = Guid.NewGuid();
        var notebookRootA = Path.Combine(storageRoot, "proj", "a");
        var notebookRootB = Path.Combine(storageRoot, "proj", "b");
        Directory.CreateDirectory(notebookRootA);
        Directory.CreateDirectory(notebookRootB);
        File.WriteAllText(Path.Combine(notebookRootB, "Collision"), "x");

        await using var db = CreateContext();
        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookA)).Returns(notebookRootA);
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookB)).Returns(notebookRootB);

        var result = await _validator.ValidateCollisionsAsync(
            db,
            pathResolver.Object,
            projectId,
            [notebookA, notebookB],
            "Collision");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_filesystem_collision");
    }

    [TestMethod]
    public async Task ValidateCollisionsAsync_RejectsNotebookFileCollision()
    {
        var storageRoot = CreateTempDirectory();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);

        await using var db = CreateContext();
        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            ProjectId = projectId,
            Title = "Test",
            Slug = "test"
        });
        db.NotebookFiles.Add(new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "Shared/readme.txt",
            FileSize = 1,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "abc"
        });
        db.NotebookFiles.Local.First().GenerateDocumentId(notebookId);
        await db.SaveChangesAsync();

        var pathResolver = CreatePathResolver(storageRoot, projectId, notebookId, notebookRoot);

        var result = await _validator.ValidateCollisionsAsync(
            db,
            pathResolver.Object,
            projectId,
            [notebookId],
            "Shared");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("leaf_notebook_file_collision");
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Mock<IStoragePathResolver> CreatePathResolver(
        string storageRoot,
        Guid projectId,
        Guid notebookId,
        string notebookRoot)
    {
        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetStorageRoot()).Returns(storageRoot);
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);
        return pathResolver;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hfm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
