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
}
