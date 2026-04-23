using Guid = System.Guid;

namespace GuideAntsApi.BackgroundJobs.Services.Indexing;

public interface IHybridIndexer
{
    Task IndexContentFileAsync(Guid contentFileVersionId, Guid projectId, string filePath, CancellationToken ct);
    Task IndexNotebookFileAsync(Guid notebookFileId, Guid notebookId, Guid projectId, string filePath, CancellationToken ct);
    Task IndexAssistantFolderAsync(string storeId, string folderPath, CancellationToken ct);
    Task IndexAssistantFileAsync(Guid fileId, Guid assistantId, string filePath, CancellationToken cancellationToken = default);
}
