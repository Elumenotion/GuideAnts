namespace GuideAntsApi.Services.Components;

/// <summary>
/// Synchronizes a notebook's physical file system directory with the database representation
/// stored in the <see cref="DataModel.Models.NotebookFile"/> table.
/// </summary>
public interface INotebookFileSyncService
{
    /// <summary>
    /// Fast register: stat + placeholder hash + immediate save. No index enqueue.
    /// </summary>
    Task RegisterFilesAsync(Guid notebookId, IReadOnlyList<string> dbRelativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans the physical directory for the given notebook and reconciles it with the database.
    /// </summary>
    Task ReconcileNotebookAsync(Guid notebookId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs an immediate full reconcile with locking to prevent concurrent operations.
    /// </summary>
    Task ReconcileNotebookImmediateAsync(Guid notebookId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues full notebook reconciliation for background processing.
    /// </summary>
    Task QueueReconcileAsync(Guid notebookId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obsolete alias for <see cref="ReconcileNotebookAsync"/>.
    /// </summary>
    [Obsolete("Use ReconcileNotebookAsync")]
    Task SyncNotebookAsync(Guid notebookId);

    /// <summary>
    /// Obsolete alias for <see cref="ReconcileNotebookImmediateAsync"/>.
    /// </summary>
    [Obsolete("Use ReconcileNotebookImmediateAsync")]
    Task SyncNotebookImmediateAsync(Guid notebookId);

    /// <summary>
    /// Obsolete alias for <see cref="QueueReconcileAsync"/>.
    /// </summary>
    [Obsolete("Use QueueReconcileAsync")]
    Task QueueNotebookSyncAsync(Guid notebookId, CancellationToken cancellationToken = default);
}
