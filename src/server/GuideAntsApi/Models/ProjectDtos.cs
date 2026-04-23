using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.Models;

public record CreateProjectDto(
    [Required] string Title,
    string? Description
);

public record UpdateProjectDto(
    [Required] string Title,
    string? Description
);

public record ProjectDto(
    Guid Id,
    string Title,
    string Description,
    DateTime Created,
    Guid? HomePageContentFileId,
    bool CanCreateContent
);

public record ProjectDetailsDto(
    Guid Id,
    string Title,
    string Description,
    DateTime Created,
    Guid? HomePageContentFileId,
    IEnumerable<NotebookDto> Notebooks,
    IEnumerable<ContentFileDto> ContentFiles,
    IEnumerable<LinkDto> Links,
    IEnumerable<SemiStructuredProjectDataDto> SemiStructuredDatas,
    bool CanCreateContent
);

public record NotebookDto(
    Guid Id,
    string Title,
    string? Description,
    Guid GuideId, // Either GuideId or resolved from NotebookTemplateId
    Guid? HomePageFileId,
    DateTime LastActivity
);

public record ContentFileDto(
    Guid Id,
    string FileName,
    string Path
);

public record LinkDto(
    Guid Id,
    string Url
);

public record SemiStructuredProjectDataDto(
    Guid Id,
    string Placeholder
);

public record CopyNotebookDto(string Title, Guid SourceNotebookId, Guid? SourceConversationMessageId); 