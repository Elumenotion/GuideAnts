using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Models;

public record ProjectFolderDto(
    Guid Id,
    string Name,
    string RelativePath,
    Guid? ParentFolderId,
    DateTime Created,
    DateTime? Modified,
    int FileCount,
    int SubFolderCount
);

public record CreateFolderDto(
    string Name,
    Guid? ParentFolderId
);

public record UpdateFolderDto(
    string? Name,
    Guid? ParentFolderId
);

public record FolderTreeDto(
    Guid Id,
    string Name,
    string RelativePath,
    List<FolderTreeDto> SubFolders,
    List<ContentFileDetailsDto> Files,
    bool IsHostMount = false,
    Guid? MountId = null,
    HostFolderMountStatus? MountStatus = null,
    bool IsLinked = false
);

public record MoveFileDto(
    Guid FileId,
    Guid? DestinationFolderId
);

public record MoveFolderDto(
    Guid FolderId,
    Guid? NewParentFolderId
); 