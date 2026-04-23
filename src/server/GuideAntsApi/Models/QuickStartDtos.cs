namespace GuideAntsApi.Models;

public record QuickStartResultDto(
    Guid ProjectId,
    string ProjectTitle,
    Guid NotebookId,
    string NotebookTitle,
    Guid ConversationId,
    string ConversationTitle
);
