namespace GuideAntsApi.Models.SystemGuide;

public sealed record SystemGuideSessionDto(
    Guid PublishedGuideId,
    Guid ProjectId,
    Guid NotebookId,
    Guid GuideId,
    string GuideName,
    string ClientBridgeId,
    bool IsAdminGuide,
    bool CommandMode);

public sealed record SystemGuideWorkspaceDto(
    Guid ProjectId,
    string ProjectSlug);
