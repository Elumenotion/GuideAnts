namespace GuideAntsApi.BackgroundJobs.Sync;

public enum ReconcileMode
{
    Full,
}

public sealed class ReconcileResult
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Removed { get; init; }
    public int IndexJobsEnqueued { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
}

/// <summary>
/// Single reconciler contract shared by API sync service and background job handler.
/// </summary>
public interface INotebookFileReconciler
{
    Task<ReconcileResult> ReconcileNotebookAsync(
        Guid notebookId,
        ReconcileMode mode = ReconcileMode.Full,
        CancellationToken cancellationToken = default);

    Task RegisterFilesAsync(
        Guid notebookId,
        IReadOnlyList<string> dbRelativePaths,
        CancellationToken cancellationToken = default);
}
