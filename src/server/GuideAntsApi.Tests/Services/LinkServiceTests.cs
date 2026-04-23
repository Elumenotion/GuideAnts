using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class LinkServiceTests
{
    private ApplicationDbContext _context = null!;
    private LinkService _service = null!;
    private readonly Guid _projectId = Guid.NewGuid();

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);

        var scopeFactory = CreateScopeFactory(_context);
        _service = new LinkService(scopeFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private Link AddLinkToDb(Guid projectId, string url = "https://example.com")
    {
        var link = new Link { ProjectId = projectId, Url = url };
        _context.Links.Add(link);
        _context.SaveChanges();
        return link;
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
    public async Task CreateAsync_CreatesLink()
    {
        var dto = await _service.CreateAsync(_projectId, "https://example.com");

        dto.Url.Should().Be("https://example.com");
        _context.Links.Should().ContainSingle(l => l.Url == "https://example.com" && l.ProjectId == _projectId);
    }

    [TestMethod]
    public async Task UpdateAsync_WithWrongProjectId_ReturnsNull()
    {
        var link = AddLinkToDb(_projectId);

        var result = await _service.UpdateAsync(Guid.NewGuid(), link.Id, "https://updated.com");

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task DeleteAsync_WhenLinkNotFound_ReturnsFalse()
    {
        var result = await _service.DeleteAsync(_projectId, Guid.NewGuid());

        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task DeleteAsync_DeletesLink()
    {
        var link = AddLinkToDb(_projectId);

        var result = await _service.DeleteAsync(_projectId, link.Id);

        result.Should().BeTrue();
        _context.Links.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAsync_WithWrongProjectId_ReturnsNull()
    {
        var link = AddLinkToDb(_projectId);

        var result = await _service.GetAsync(Guid.NewGuid(), link.Id);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetAllForProjectAsync_FiltersByProjectId()
    {
        AddLinkToDb(_projectId, "https://l1.com");
        AddLinkToDb(Guid.NewGuid(), "https://other.com");

        var result = (await _service.GetAllForProjectAsync(_projectId)).ToList();

        result.Should().HaveCount(1);
        result[0].Url.Should().Be("https://l1.com");
    }
}