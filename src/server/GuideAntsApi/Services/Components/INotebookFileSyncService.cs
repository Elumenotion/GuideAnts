namespace GuideAntsApi.Services.Components;

/// <summary>
/// Synchronizes a notebook's physical file system directory with the database representation
/// stored in the <see cref="DataModel.Models.NotebookFile"/> table.
/// </summary>
public interface INotebookFileSyncService
{
    /// <summary>
    /// Scans the physical directory for the given notebook and reconciles it with the database.
    /// </summary>
    /// <param name="notebookId">Notebook identifier.</param>
    Task SyncNotebookAsync(Guid notebookId);
    
    /// <summary>
    /// Performs an immediate sync with locking to prevent concurrent operations.
    /// Safe to call frequently - skips if another sync is already running.
    /// Never throws exceptions.
    /// </summary>
    /// <param name="notebookId">Notebook identifier.</param>
    Task SyncNotebookImmediateAsync(Guid notebookId);

    /// <summary>
    /// Enqueues notebook reconciliation for background processing.
    /// Prefer <see cref="SyncNotebookAsync"/> after tool or assistant writes so
    /// <c>NotebookFiles</c> rows exist before the client requests file content; use this
    /// when a full sync is optional or already ran synchronously.
    /// </summary>
    /// <param name="notebookId">Notebook identifier.</param>
    /// <param name="cancellationToken">Cancellation token for the enqueue operation.</param>
    Task QueueNotebookSyncAsync(Guid notebookId, CancellationToken cancellationToken = default);
} 
