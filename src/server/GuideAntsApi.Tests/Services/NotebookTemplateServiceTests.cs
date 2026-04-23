using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class NotebookTemplateServiceTests
{
    private ApplicationDbContext _context = null!;
    private NotebookTemplateService _service = null!;
    private Mock<ILogger<NotebookTemplateService>> _loggerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(dbOptions);

        _loggerMock = new Mock<ILogger<NotebookTemplateService>>();
        var scopeFactory = new GuideAntsApi.Tests.TestUtils.TestServiceScopeFactory(_context);
        _service = new NotebookTemplateService(scopeFactory, _loggerMock.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [TestMethod]
    public async Task GetTemplateSummariesAsync_ReturnsDatabaseGuidesOnly()
    {
        var guide = new Assistant
        {
            Name = "Sample Template",
            Description = "From DB",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true,
            AvatarImageBytes = [0x89, 0x50],
            AuthConfigJson = """
                             { "providers": [ { "id": "graph.microsoft.com", "authType": "oauth", "userConfigPolicy": "optional" } ] }
                             """
        };
        _context.Assistants.Add(guide);
        await _context.SaveChangesAsync();

        var templates = (await _service.GetTemplateSummariesAsync()).ToList();

        templates.Should().HaveCount(1);
        templates[0].TemplateName.Should().Be("Sample Template");
        templates[0].Description.Should().Be("From DB");
        templates[0].AvatarUrl.Should().Contain("/api/notebook-templates/avatar/");
        templates[0].HasConfigurableAuth.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetTemplatesAsync_ReturnsGuideBackedTemplate()
    {
        var guide = new Assistant
        {
            Name = "Code Notebook",
            Description = "DB guide",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true,
            HomePageMarkdown = "# Home"
        };
        _context.Assistants.Add(guide);
        await _context.SaveChangesAsync();

        _context.AssistantConversationStarters.AddRange(
            new AssistantConversationStarter { AssistantId = guide.Id, Prompt = "Starter 1", OrderIndex = 0 },
            new AssistantConversationStarter { AssistantId = guide.Id, Prompt = "Starter 2", OrderIndex = 1 });
        await _context.SaveChangesAsync();

        var templates = (await _service.GetTemplatesAsync()).ToList();

        templates.Should().HaveCount(1);
        templates[0].TemplateName.Should().Be("Code Notebook");
        templates[0].Description.Should().Be("DB guide");
        templates[0].DefaultAssistant.Should().Be("Code Notebook");
        templates[0].ConversationStarters.Should().BeEquivalentTo(["Starter 1", "Starter 2"]);
        templates[0].HomeContent.Should().Be("# Home");
    }

    [TestMethod]
    public async Task GetTemplatesAsync_DoesNotReturnLegacyNotebookTemplateWithoutGuide()
    {
        _context.NotebookTemplates.Add(new NotebookTemplate
        {
            TemplateName = "Legacy-Only Template"
        });
        await _context.SaveChangesAsync();

        var templates = (await _service.GetTemplatesAsync()).ToList();

        templates.Should().BeEmpty();
    }
}
