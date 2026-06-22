using GuideAntsApi.DataModel;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Endpoints;

namespace GuideAntsApi.Services;

public interface IQuickStartService
{
    Task<QuickStartResultDto> CreateQuickStartProjectAsync();
}

public class QuickStartService : IQuickStartService
{
    private readonly IProjectService _projectService;
    private readonly INotebookService _notebookService;
    private readonly IConversationService _conversationService;
    private readonly INotebookTemplateService _templateService;
    private readonly ApplicationDbContext _context;

    public QuickStartService(
        IProjectService projectService,
        INotebookService notebookService,
        IConversationService conversationService,
        INotebookTemplateService templateService,
        ApplicationDbContext context)
    {
        _projectService = projectService;
        _notebookService = notebookService;
        _conversationService = conversationService;
        _templateService = templateService;
        _context = context;
    }

    public async Task<QuickStartResultDto> CreateQuickStartProjectAsync()
    {
        // Get user information
        var userId = Guid.Empty;
        var userName = "User";

        // Create project
        var projectDto = new CreateProjectDto(
            Title: $"{userName}'s Quick Start Project",
            Description: "A starter project to help you learn ways to use GuideAnts Notebooks"
        );

        var project = await _projectService.CreateProjectAsync(projectDto);

        // Get Creative Guide template for this user
        var templates = await _templateService.GetTemplatesAsync(project.Id, userId);
        var creativeGuideTemplate = templates.FirstOrDefault(t => t.TemplateName == "Creative Guide");
        if (creativeGuideTemplate == null)
        {
            throw new InvalidOperationException("Creative Guide template not found");
        }

        // Create notebook with Creative Guide
        var notebookDto = new CreateNotebookDto
        {
            Title = "Getting Started",
            Description = "Your first notebook to explore GuideAnts capabilities",
            GuideId = creativeGuideTemplate.Id
        };

        var notebook = await _notebookService.CreateAsync(project.Id, notebookDto);

        // Create conversation
        var conversation = await _conversationService.CreateConversationAsync(notebook.Id, "New Conversation");

        return new QuickStartResultDto(
            ProjectId: project.Id,
            ProjectTitle: project.Title,
            NotebookId: notebook.Id,
            NotebookTitle: notebook.Title,
            ConversationId: conversation.Id,
            ConversationTitle: conversation.Title
        );
    }
}
