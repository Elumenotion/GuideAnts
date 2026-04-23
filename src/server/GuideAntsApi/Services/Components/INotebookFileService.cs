using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public interface INotebookFileService
{
    Task<IEnumerable<NotebookFileDto>> ListFilesAsync(Guid projectId, Guid notebookId);
    Task<NotebookFolderTreeDto?> GetFolderTreeAsync(Guid projectId, Guid notebookId);
    Task<(Stream Stream, string ContentType, string FileName)?> GetFileAsync(Guid projectId, Guid notebookId, string relativePath);
    Task<(Stream stream, string contentType)> GetFileContentStreamAsync(Guid projectId, Guid notebookId, string relativePath);
    Task<(Stream Stream, string ContentType, string FileName)?> GetFileContentStreamAsync(Guid notebookFileId, CancellationToken cancellationToken = default);
    Task<NotebookFileDto?> CopyFromProjectAsync(Guid projectId, Guid notebookId, Guid contentFileId, int? versionNumber, string? targetRelativePath);
    Task<IEnumerable<NotebookFileDto>> UploadFilesAsync(Guid projectId, Guid notebookId, IFormFileCollection files, string targetRelativePath, bool index = false);
    Task<NotebookFolderTreeDto?> CreateFolderAsync(Guid projectId, Guid notebookId, string newFolderPath);
    Task<bool> DeleteAsync(Guid projectId, Guid notebookId, string relativePath);
    Task<bool> RenameAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string newName);
    Task<bool> MoveAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string destinationRelativePath);
    
    // By-ID operations (no tree lookup required)
    Task<bool> DeleteByIdAsync(Guid projectId, Guid notebookId, Guid fileId);
    Task<bool> RenameByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string newName);
    Task<bool> MoveByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string? destinationPath);
    
    Task<ContentFileDetailsDto> PublishToProjectAsync(Guid projectId, Guid notebookId, Guid notebookFileId, Guid? destinationFolderId, bool index);
    Task<GuideAntsApi.Endpoints.OriginFileInfoDto?> GetOriginFileInfoAsync(Guid projectId, Guid contentFileVersionId);
    Task<NotebookFileDto> CreateTextFileAsync(Guid projectId, Guid notebookId, string relativePath, string content);
} 