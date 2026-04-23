namespace GuideAntsApi.Models.Guides;

// Preview of what will be imported
public record ImportPreviewDto(
    string GuideName,
    int CrewCount,
    int CustomAssistantCount,
    List<string> NameConflicts
);

// Result of import operation
public record ImportGuideResultDto(
    bool Success,
    Guid? GuideId,
    int CustomAssistantsCreated,
    int CrewsCreated,
    int GlobalAssistantsLinked,
    int ItemsSkipped,
    List<string> Warnings
);

