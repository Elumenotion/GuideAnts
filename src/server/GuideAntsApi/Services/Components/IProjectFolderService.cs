using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public interface IProjectFolderService
{
    Task<ProjectFolderDto> CreateFolderAsync(Guid projectId, CreateFolderDto dto);
    Task<ProjectFolderDto?> UpdateFolderAsync(Guid projectId, Guid folderId, UpdateFolderDto dto);
    Task<bool> DeleteFolderAsync(Guid projectId, Guid folderId);
    Task<IEnumerable<ProjectFolderDto>> GetFoldersAsync(Guid projectId);
    Task<FolderTreeDto> GetFolderTreeAsync(Guid projectId);
    Task<bool> MoveFolderAsync(Guid projectId, Guid folderId, Guid? newParentId);
    Task<ProjectFolderDto?> GetFolderAsync(Guid projectId, Guid folderId);
} 