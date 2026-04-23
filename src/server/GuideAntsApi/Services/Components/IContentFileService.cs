using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public interface IContentFileService
{
    Task<ContentFileDetailsDto> UploadFileAsync(Guid projectId, IFormFile file, bool index = false, Guid? folderId = null);
    Task<ContentFileDetailsDto?> MoveFileAsync(Guid projectId, Guid fileId, Guid? destinationFolderId);
    Task<bool> DeleteAsync(Guid projectId, Guid fileId);
    Task<ContentFileDetailsDto?> GetAsync(Guid projectId, Guid fileId);
    Task<IEnumerable<ContentFileDetailsDto>> GetAllForProjectAsync(Guid projectId);
    Task<ContentFileDetailsDto?> UpdateAsync(Guid projectId, Guid fileId, UpdateContentFileDto updates);
    Task<ContentFileContentDto?> GetContentAsync(Guid projectId, Guid fileId);
    Task<IEnumerable<ContentFileVersionDto>> GetVersionsAsync(Guid projectId, Guid fileId);
    Task<ContentFileContentDto?> GetVersionContentAsync(Guid projectId, Guid fileId, int versionNumber);
    Task<ContentFileDetailsDto> CreateFileFromPathAsync(Guid projectId, string sourcePath, string fileName, string contentType, Guid? folderId, Guid originNotebookFileId, bool index = false);
    Task<ContentFileDetailsDto> CreateVersionFromPathAsync(Guid projectId, Guid contentFileId, string sourcePath, Guid originNotebookFileId, bool index = false);
} 