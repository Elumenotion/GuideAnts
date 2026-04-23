using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class ProjectServiceTests
{
    private ApplicationDbContext _context = null!;
    private ProjectService _projectService = null!;
    private Mock<IConfiguration> _configurationMock = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);

        _configurationMock = new Mock<IConfiguration>();
        _configurationMock.Setup(x => x["FileStorage:Path"]).Returns("C:\\temp\\test-storage");

        var scopeFactory = CreateScopeFactory(_context);
        _projectService = new ProjectService(scopeFactory, _configurationMock.Object, NullLogger<ProjectService>.Instance);
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

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);
        return scopeFactoryMock.Object;
    }

    [TestMethod]
    public async Task CreateProject_WithValidData_CreatesProject()
    {
        var result = await _projectService.CreateProjectAsync(new CreateProjectDto("Test Project", "Test Description"));

        result.Should().NotBeNull();
        result.Title.Should().Be("Test Project");
        (await _context.Projects.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task GetProject_WhenDeleted_ReturnsNull()
    {
        var project = new Project { Title = "Test", Description = "Desc", Deleted = true };
        _context.Projects.Add(project);
        await _context.SaveChangesAsync();

        var result = await _projectService.GetProjectAsync(project.Id);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task UpdateProject_WhenExists_UpdatesProject()
    {
        var project = new Project { Title = "Old", Description = "Old Desc" };
        _context.Projects.Add(project);
        await _context.SaveChangesAsync();

        var result = await _projectService.UpdateProjectAsync(project.Id, new UpdateProjectDto("New", "New Desc"));

        result.Should().NotBeNull();
        result!.Title.Should().Be("New");
        var db = await _context.Projects.FindAsync(project.Id);
        db!.Title.Should().Be("New");
    }

    [TestMethod]
    public async Task UpdateProject_WhenMissing_ReturnsNull()
    {
        var result = await _projectService.UpdateProjectAsync(Guid.NewGuid(), new UpdateProjectDto("New", "Desc"));

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task DeleteProject_WhenEmpty_RemovesProject()
    {
        var project = new Project { Title = "P", Description = "D" };
        _context.Projects.Add(project);
        await _context.SaveChangesAsync();

        var result = await _projectService.DeleteProjectAsync(project.Id);

        result.Should().BeTrue();
        (await _context.Projects.FindAsync(project.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task DeleteProject_WhenHasContent_HardDeletesProject()
    {
        var project = new Project { Title = "P", Slug = "p", Description = "D" };
        _context.Projects.Add(project);
        _context.Notebooks.Add(new Notebook { Title = "NB", Slug = "nb", Project = project });
        await _context.SaveChangesAsync();

        var result = await _projectService.DeleteProjectAsync(project.Id);

        result.Should().BeTrue();
        var db = await _context.Projects.FindAsync(project.Id);
        db.Should().BeNull();
    }

    [TestMethod]
    public async Task DeleteProject_WhenMissing_ReturnsFalse()
    {
        var result = await _projectService.DeleteProjectAsync(Guid.NewGuid());

        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetProjects_ExcludesDeletedProjects()
    {
        _context.Projects.Add(new Project { Title = "Active", Description = "A", Deleted = false });
        _context.Projects.Add(new Project { Title = "Deleted", Description = "D", Deleted = true });
        await _context.SaveChangesAsync();

        var result = (await _projectService.GetProjectsAsync()).ToList();

        result.Should().ContainSingle();
        result[0].Title.Should().Be("Active");
    }
}
