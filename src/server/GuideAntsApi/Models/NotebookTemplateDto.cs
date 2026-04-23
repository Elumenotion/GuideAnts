namespace GuideAntsApi.Models;

/// <summary>
/// Lightweight DTO for listing templates in selection dialogs.
/// Contains only the essential fields needed for display.
/// </summary>
/// <param name="Id">The template/guide ID.</param>
/// <param name="TemplateName">The display name of the template.</param>
/// <param name="Description">A short description of the template.</param>
/// <param name="AvatarUrl">URL to the template's avatar image.</param>
/// <param name="HasConfigurableAuth">True if this template has auth providers with optional or required user configuration.</param>
public record NotebookTemplateSummaryDto(
    Guid Id,
    string TemplateName,
    string Description,
    string AvatarUrl,
    bool HasConfigurableAuth = false);

/// <summary>
/// Full DTO with all template details including home page content.
/// Use GetTemplateByIdAsync to retrieve full details for a specific template.
/// </summary>
public record NotebookTemplateDto(
    Guid Id,
    string TemplateName,
    string Description,
    string AvatarUrl,
    List<string> ConversationStarters,
    string? DefaultAssistant,
    string? HomeContent,
    List<NotebookAuthProviderDto>? AuthProviders);