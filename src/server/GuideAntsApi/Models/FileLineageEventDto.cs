using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Models;

public record FileLineageEventDto(
    Guid Id,
    FileLineageAction Action,
    FileKind FileKind,
    Guid FileId,
    int? VersionNumber,
    Guid ProjectId,
    Guid? NotebookId,
    string? StoragePath,
    DateTime Timestamp,
    string UserId,
    string UserDisplayName,
    string FileName,
    string FileVersionLabel,
    string? NotebookName); 