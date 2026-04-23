using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.LlamaCpp;

public class NotebookLlamaRuntimeStatusDto
{
    public string State { get; set; } = "ready"; // ready, requires_load, loading, failed, invalid
    public Guid? SelectedAssistantId { get; set; }
    public List<ModelDto> RequiredModels { get; set; } = new();
    public List<ModelDto> LoadedModels { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public ModelLoadOperationDto? ActiveOperation { get; set; }
}

public class ModelLoadOperationDto
{
    public string OperationId { get; set; } = string.Empty;
    public string State { get; set; } = "queued"; // queued, unloading, loading, verifying, ready, failed
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? TargetAssistantId { get; set; }
    public string? ErrorDetails { get; set; }
}

public interface INotebookModelRuntimeService
{
    Task<NotebookLlamaRuntimeStatusDto> GetRuntimeStatusAsync(Guid notebookId, Guid? assistantId = null, CancellationToken cancellationToken = default);
    Task<ModelLoadOperationDto> StartLoadOperationAsync(Guid notebookId, Guid? assistantId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads the llama router models required for this notebook context, releasing memory
    /// (workspace-wide within the shared llama-server).
    /// </summary>
    Task<ModelLoadOperationDto> StartUnloadForNotebookContextAsync(
        Guid notebookId,
        Guid? assistantId = null,
        CancellationToken cancellationToken = default);

    Task<ModelLoadOperationDto?> GetOperationStatusAsync(Guid notebookId, string operationId, CancellationToken cancellationToken = default);
}