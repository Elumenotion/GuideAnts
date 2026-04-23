namespace GuideAntsApi.Models.Conversations;

public record UserConversationDto(
    Guid Id,
    string Title,
    Guid NotebookId,
    string NotebookTitle,
    Guid ProjectId,
    string ProjectTitle,
    DateTime Created,
    DateTime? LastActivity
);






