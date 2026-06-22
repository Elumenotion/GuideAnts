using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class NotebookServiceTests
{
    private ApplicationDbContext _context = null!;
    private Mock<IContentFileService> _contentFileServiceMock = null!;
    private Mock<INotebookFileService> _notebookFileServiceMock = null!;
    private Mock<IConfiguration> _configurationMock = null!;
    private NotebookService _service = null!;
    private Guid _projectId;
    private Assistant _defaultGuide = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new ApplicationDbContext(options);

        _projectId = Guid.NewGuid();
        _context.Projects.Add(new Project { Id = _projectId, Title = "Test Project" });

        _defaultGuide = new Assistant
        {
            Id = Guid.NewGuid(),
            Name = "Test Guide",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true
        };
        _context.Assistants.Add(_defaultGuide);
        _context.SaveChanges();

        _contentFileServiceMock = new Mock<IContentFileService>();
        _notebookFileServiceMock = new Mock<INotebookFileService>();

        _configurationMock = new Mock<IConfiguration>();
        _configurationMock.Setup(c => c["FileStorage:Path"]).Returns(Path.Combine(Path.GetTempPath(), "guideants_test"));

        var scopeFactory = CreateScopeFactory(_context);
        _service = new NotebookService(
            scopeFactory,
            _contentFileServiceMock.Object,
            _notebookFileServiceMock.Object,
            _configurationMock.Object,
            NullLogger<NotebookService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private static IServiceScopeFactory CreateScopeFactory(ApplicationDbContext context)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(context);
        providerMock.Setup(p => p.GetService(typeof(GuideAntsApi.Services.SystemGuide.ISystemGuideCatalogFilter)))
            .Returns(GuideAntsApi.Tests.TestUtils.EmptySystemGuideCatalogFilter.Instance);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);
        return scopeFactoryMock.Object;
    }

    private Notebook AddNotebookToDb(Guid projectId, string title = "Test Notebook")
    {
        var notebook = new Notebook
        {
            ProjectId = projectId,
            Title = title,
            GuideId = _defaultGuide.Id
        };
        _context.Notebooks.Add(notebook);
        _context.SaveChanges();
        return notebook;
    }

    [TestMethod]
    public async Task CreateAsync_WithMissingGuideId_ThrowsArgumentException()
    {
        var createDto = new CreateNotebookDto { Title = "Test Notebook", GuideId = null };

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _service.CreateAsync(_projectId, createDto));
    }

    [TestMethod]
    public async Task CreateAsync_WithValidCreateNotebookDto_CreatesNotebook()
    {
        var createDto = new CreateNotebookDto
        {
            Title = "Test Notebook",
            GuideId = _defaultGuide.Id
        };

        var result = await _service.CreateAsync(_projectId, createDto);

        result.Should().NotBeNull();
        result.Title.Should().Be("Test Notebook");
        var notebookInDb = await _context.Notebooks.FirstOrDefaultAsync(n => n.Id == result.Id);
        notebookInDb.Should().NotBeNull();
        notebookInDb!.ProjectId.Should().Be(_projectId);
    }

    [TestMethod]
    public async Task UpdateAsync_WithNonExistentNotebook_ThrowsKeyNotFoundException()
    {
        var updateDto = new UpdateNotebookDto { Title = "Updated" };

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => _service.UpdateAsync(_projectId, Guid.NewGuid(), updateDto));
    }

    [TestMethod]
    public async Task DeleteAsync_WithNonExistentNotebook_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => _service.DeleteAsync(_projectId, Guid.NewGuid()));
    }

    [TestMethod]
    public async Task GetAsync_WithWrongProject_ReturnsNull()
    {
        var notebook = AddNotebookToDb(_projectId);

        var result = await _service.GetAsync(Guid.NewGuid(), notebook.Id);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetAllForProjectAsync_FiltersByProject()
    {
        AddNotebookToDb(_projectId, "Notebook 1");
        AddNotebookToDb(Guid.NewGuid(), "Notebook 2");

        var result = (await _service.GetAllForProjectAsync(_projectId)).ToList();

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Notebook 1");
    }

    [TestMethod]
    public async Task CreateNotebookFromFileAsync_WithValidData_CreatesNotebookAndCopiesFile()
    {
        var contentFileId = Guid.NewGuid();
        var copiedFileId = Guid.NewGuid();
        var dto = new CreateNotebookFromFileDto("Test Notebook", contentFileId, 1, _defaultGuide.Id.ToString());

        var expectedCopiedFile = new NotebookFileDto(
            copiedFileId,
            "copied.txt",
            "copied.txt",
            12,
            DateTime.UtcNow,
            "hash",
            null,
            false,
            false);

        _notebookFileServiceMock
            .Setup(s => s.CopyFromProjectAsync(_projectId, It.IsAny<Guid>(), contentFileId, 1, null))
            .ReturnsAsync(expectedCopiedFile);

        var result = await _service.CreateNotebookFromFileAsync(_projectId, dto);

        result.Should().NotBeNull();
        result.CopiedFileId.Should().Be(copiedFileId);
        result.NotebookTitle.Should().Be("Test Notebook");
    }

    [TestMethod]
    public async Task CreateNotebookFromFileAsync_WhenFileCopyFails_ThrowsInvalidOperationException()
    {
        var contentFileId = Guid.NewGuid();
        var dto = new CreateNotebookFromFileDto("Test Notebook", contentFileId, 1, _defaultGuide.Id.ToString());

        _notebookFileServiceMock
            .Setup(s => s.CopyFromProjectAsync(_projectId, It.IsAny<Guid>(), contentFileId, 1, null))
            .ReturnsAsync((NotebookFileDto?)null);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.CreateNotebookFromFileAsync(_projectId, dto));
    }
}
