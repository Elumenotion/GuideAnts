using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class ProjectFolderServiceTests
{
    private ApplicationDbContext _context = null!;
    private Mock<IConfiguration> _configurationMock = null!;
    private ProjectFolderService _service = null!;
    private Guid _projectId;
    private string _tempDirectory = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _configurationMock = new Mock<IConfiguration>();
        _tempDirectory = Path.Combine(Path.GetTempPath(), "guideants_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDirectory);
        _configurationMock.Setup(c => c["FileStorage:Path"]).Returns(_tempDirectory);

        _projectId = Guid.NewGuid();
        _context.Projects.Add(new Project { Id = _projectId, Title = "Test Project" });
        _context.SaveChanges();

        var scopeFactory = CreateScopeFactory(_context);
        _service = new ProjectFolderService(scopeFactory, _configurationMock.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
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

    [TestMethod]
    public async Task CreateFolderAsync_WithValidData_CreatesFolder()
    {
        var dto = new CreateFolderDto("TestFolder", null);

        var result = await _service.CreateFolderAsync(_projectId, dto);

        result.Name.Should().Be("TestFolder");
        var physicalPath = Path.Combine(_tempDirectory, _projectId.ToString(), "TestFolder");
        Directory.Exists(physicalPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateFolderAsync_WithDuplicatePath_ThrowsArgumentException()
    {
        await _service.CreateFolderAsync(_projectId, new CreateFolderDto("TestFolder", null));

        await _service.Invoking(s => s.CreateFolderAsync(_projectId, new CreateFolderDto("TestFolder", null)))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("Folder already exists at this path");
    }

    [TestMethod]
    public async Task UpdateFolderAsync_WithNewName_UpdatesFolder()
    {
        var folder = new ProjectFolder
        {
            Name = "Original",
            RelativePath = "Original",
            ProjectId = _projectId
        };
        _context.ProjectFolders.Add(folder);
        await _context.SaveChangesAsync();

        var result = await _service.UpdateFolderAsync(_projectId, folder.Id, new UpdateFolderDto("Updated", null));

        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated");
    }

    [TestMethod]
    public async Task DeleteFolderAsync_FolderWithFiles_ThrowsInvalidOperationException()
    {
        var folder = new ProjectFolder
        {
            Name = "Folder",
            RelativePath = "Folder",
            ProjectId = _projectId
        };
        _context.ProjectFolders.Add(folder);
        await _context.SaveChangesAsync();

        _context.ContentFiles.Add(new ContentFile
        {
            Id = Guid.NewGuid(),
            FileName = "file.txt",
            Path = "file.txt",
            RelativePath = "file.txt",
            FileSize = 1,
            ContentType = "text/plain",
            Created = DateTime.UtcNow,
            ProjectId = _projectId,
            FolderId = folder.Id
        });
        await _context.SaveChangesAsync();

        await _service.Invoking(s => s.DeleteFolderAsync(_projectId, folder.Id))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task MoveFolderAsync_CircularReference_ReturnsFalse()
    {
        var parent = new ProjectFolder
        {
            Name = "Parent",
            RelativePath = "Parent",
            ProjectId = _projectId
        };
        var child = new ProjectFolder
        {
            Name = "Child",
            RelativePath = "Parent/Child",
            ProjectId = _projectId,
            ParentFolder = parent
        };

        _context.ProjectFolders.AddRange(parent, child);
        await _context.SaveChangesAsync();

        var result = await _service.MoveFolderAsync(_projectId, parent.Id, child.Id);

        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetFolderTreeAsync_WithNestedStructure_ReturnsCorrectTree()
    {
        var parent = new ProjectFolder { Name = "Parent", RelativePath = "Parent", ProjectId = _projectId };
        _context.ProjectFolders.Add(parent);
        await _context.SaveChangesAsync();

        var child = new ProjectFolder { Name = "Child", RelativePath = "Parent/Child", ProjectId = _projectId, ParentFolderId = parent.Id };
        _context.ProjectFolders.Add(child);
        await _context.SaveChangesAsync();

        var tree = await _service.GetFolderTreeAsync(_projectId);

        tree.Should().NotBeNull();
        tree.SubFolders.Should().ContainSingle(f => f.Name == "Parent");
    }

    [TestMethod]
    public async Task GetFolderTreeAsync_ProjectMountScan_IsCachedWithinTtl()
    {
        // Arrange: a project-scope host mount pointing at a temp directory with one file.
        var mountDir = Path.Combine(_tempDirectory, "mount_source");
        Directory.CreateDirectory(mountDir);
        await File.WriteAllTextAsync(Path.Combine(mountDir, "a.txt"), "a");

        _context.HostFolderMounts.Add(new HostFolderMount
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Scope = HostFolderMountScope.Project,
            NotebookId = null,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "MyMount",
            LeafName = "MyMount",
            MountKey = Guid.NewGuid().ToString("N"),
            SourceSpec = @"D:\mount\source",
            ContainerSourcePath = mountDir,
            Status = HostFolderMountStatus.Active,
            CreatedByUserId = Guid.NewGuid()
        });
        await _context.SaveChangesAsync();

        // First call scans the filesystem and caches the result (default 15s TTL).
        var firstTree = await _service.GetFolderTreeAsync(_projectId);
        CollectMountFileNames(firstTree, "MyMount").Should().BeEquivalentTo(["a.txt"]);

        // Add a new file to the mount on disk after the scan was cached.
        await File.WriteAllTextAsync(Path.Combine(mountDir, "b.txt"), "b");

        // Second call within the TTL must reuse the cached scan and NOT observe b.txt.
        var secondTree = await _service.GetFolderTreeAsync(_projectId);
        CollectMountFileNames(secondTree, "MyMount").Should().BeEquivalentTo(["a.txt"]);
    }

    [TestMethod]
    public async Task GetFolderTreeAsync_ProjectMountScan_ReScansAfterTtlExpires()
    {
        // Regression guard: the cache TTL must actually be honored. A TTL shorter than the
        // poll interval means every poll re-scans; here we prove expiry triggers a fresh scan.
        var mountDir = Path.Combine(_tempDirectory, "mount_source_ttl");
        Directory.CreateDirectory(mountDir);
        await File.WriteAllTextAsync(Path.Combine(mountDir, "a.txt"), "a");

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["FileStorage:Path"]).Returns(_tempDirectory);
        configMock.Setup(c => c["FileStorage:ProjectMountTreeCacheSeconds"]).Returns("1");
        var service = new ProjectFolderService(CreateScopeFactory(_context), configMock.Object);

        _context.HostFolderMounts.Add(new HostFolderMount
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Scope = HostFolderMountScope.Project,
            NotebookId = null,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "TtlMount",
            LeafName = "TtlMount",
            MountKey = Guid.NewGuid().ToString("N"),
            SourceSpec = @"D:\mount\source",
            ContainerSourcePath = mountDir,
            Status = HostFolderMountStatus.Active,
            CreatedByUserId = Guid.NewGuid()
        });
        await _context.SaveChangesAsync();

        var firstTree = await service.GetFolderTreeAsync(_projectId);
        CollectMountFileNames(firstTree, "TtlMount").Should().BeEquivalentTo(["a.txt"]);

        await File.WriteAllTextAsync(Path.Combine(mountDir, "b.txt"), "b");

        // Wait past the 1s TTL so the next call re-scans and observes the new file.
        await Task.Delay(1200);

        var secondTree = await service.GetFolderTreeAsync(_projectId);
        CollectMountFileNames(secondTree, "TtlMount").Should().BeEquivalentTo(["a.txt", "b.txt"]);
    }

    private static List<string> CollectMountFileNames(FolderTreeDto tree, string mountLeafName)
    {
        var mountRoot = FindFolder(tree, mountLeafName);
        if (mountRoot == null)
        {
            return [];
        }

        var names = new List<string>();
        CollectFileNames(mountRoot, names);
        return names;
    }

    private static FolderTreeDto? FindFolder(FolderTreeDto node, string name)
    {
        if (string.Equals(node.Name, name, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.SubFolders)
        {
            var found = FindFolder(child, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void CollectFileNames(FolderTreeDto node, List<string> names)
    {
        names.AddRange(node.Files.Select(f => f.FileName));
        foreach (var child in node.SubFolders)
        {
            CollectFileNames(child, names);
        }
    }
}
