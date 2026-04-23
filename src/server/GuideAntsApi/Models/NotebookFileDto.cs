namespace GuideAntsApi.Models;

public record NotebookFileDto(
    Guid Id,
    string FileName,
    string RelativePath,
    long FileSize,
    DateTime LastModifiedUtc,
    string FileHash,
    Guid? OriginContentFileVersionId,
    bool Index,
    bool IsIndexed
);

public record NotebookFolderTreeDto(
    string Name,
    string RelativePath,
    List<NotebookFolderTreeDto> SubFolders,
    List<NotebookFileDto> Files
);

public record RenameItemDto(string SourcePath, string NewName);
public record MoveItemDto(string SourcePath, string DestinationPath);
public record NotebookCreateFolderDto(string Path);
