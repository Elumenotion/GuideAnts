namespace GuideAntsApi.Services;

public interface INotebookTemplateService
{
    /// <summary>
    /// Retrieve lightweight summaries of all notebook templates for selection dialogs.
    /// Does not load home page content or other heavy details.
    /// </summary>
    /// <param name="projectId">Optional project context; system guides remain visible for the system project.</param>
    /// <param name="userId">Optional user ID to prioritize user-specific templates.</param>
    Task<IEnumerable<GuideAntsApi.Models.NotebookTemplateSummaryDto>> GetTemplateSummariesAsync(
        Guid? projectId = null,
        Guid? userId = null);

    /// <summary>
    /// Retrieve all notebook templates, enriched with data from their manifests.
    /// </summary>
    /// <param name="projectId">Optional project context; system guides remain visible for the system project.</param>
    /// <param name="userId">Optional user ID to prioritize user-specific templates.</param>
    Task<IEnumerable<GuideAntsApi.Models.NotebookTemplateDto>> GetTemplatesAsync(
        Guid? projectId = null,
        Guid? userId = null);

    /// <summary>
    /// Retrieve a single notebook template by Id, enriched with manifest data.
    /// </summary>
    /// <param name="templateId">The template ID to retrieve.</param>
    /// <param name="projectId">Optional project context; system guides remain visible for the system project.</param>
    /// <param name="userId">Optional user ID for user-specific template lookup.</param>
    Task<GuideAntsApi.Models.NotebookTemplateDto?> GetTemplateByIdAsync(
        Guid templateId,
        Guid? projectId = null,
        Guid? userId = null);

    /// <summary>
    /// Returns the list of assistant definitions (crew) for a template.
    /// </summary>
    /// <param name="templateId">The template ID</param>
    /// <param name="projectId">Optional project context; system guides remain visible for the system project.</param>
    /// <param name="userId">Optional user ID for user-specific guide lookup</param>
    Task<IReadOnlyList<GuideAntsApi.Models.AssistantDefinitionDto>> GetAssistantsAsync(
        Guid templateId,
        Guid? projectId = null,
        Guid? userId = null);

}
