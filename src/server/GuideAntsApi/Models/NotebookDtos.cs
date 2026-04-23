namespace GuideAntsApi.Models;

public record CreateNotebookFromFileDto(
    string Title,
    Guid ContentFileId,
    int? VersionNumber = null,
    string? GuideId = null
);

public record CreateNotebookFromFileResultDto(
    Guid NotebookId,
    string NotebookTitle,
    Guid CopiedFileId,
    string CopiedFileName
); 