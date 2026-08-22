using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public interface IProjectFolderService
{
    Task<ProjectFolderDto> CreateFolderAsync(Guid projectId, CreateFolderDto dto);
    Task<ProjectFolderDto?> UpdateFolderAsync(Guid projectId, Guid folderId, UpdateFolderDto dto);
    Task<bool> DeleteFolderAsync(Guid projectId, Guid folderId);
    Task<IEnumerable<ProjectFolderDto>> GetFoldersAsync(Guid projectId);
    Task<FolderTreeDto> GetFolderTreeAsync(Guid projectId);
    Task<HostMountListingDto?> ListHostMountLevelAsync(Guid projectId, string relativePath);
    Task<bool> MoveFolderAsync(Guid projectId, Guid folderId, Guid? newParentId);
    Task<ProjectFolderDto?> GetFolderAsync(Guid projectId, Guid folderId);

    Task<(Stream Stream, string ContentType, string FileName)?> GetMountedFileContentAsync(Guid projectId, string relativePath);
    Task<ContentFileDetailsDto?> GetMountedFileDetailsAsync(Guid projectId, string relativePath);
    Task<bool> SaveMountedFileContentAsync(Guid projectId, string relativePath, Stream content);
    Task<bool> RenameMountedEntryAsync(Guid projectId, string relativePath, string newName);
}