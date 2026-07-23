using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.Services.Components.Sync;

namespace GuideAntsApi.Services.Components;

public class NotebookFileSyncService : INotebookFileSyncService
{
    private readonly INotebookFileReconciler _reconciler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotebookFileSyncService> _logger;

    public NotebookFileSyncService(
        INotebookFileReconciler reconciler,
        IServiceScopeFactory scopeFactory,
        ILogger<NotebookFileSyncService> logger)
    {
        _reconciler = reconciler;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task RegisterFilesAsync(
        Guid notebookId,
        IReadOnlyList<string> dbRelativePaths,
        CancellationToken cancellationToken = default) =>
        _reconciler.RegisterFilesAsync(notebookId, dbRelativePaths, cancellationToken);

    public Task ReconcileNotebookAsync(Guid notebookId, CancellationToken cancellationToken = default) =>
        _reconciler.ReconcileNotebookAsync(notebookId, ReconcileMode.Full, cancellationToken);

    public async Task ReconcileNotebookImmediateAsync(Guid notebookId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _reconciler.ReconcileNotebookAsync(notebookId, ReconcileMode.Full, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during immediate reconcile for notebook {NotebookId}", notebookId);
        }
    }

    public async Task QueueReconcileAsync(Guid notebookId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

        await jobQueue.EnqueueAsync(
            jobType: nameof(SyncNotebookJob).Replace("Job", string.Empty),
            payload: new SyncNotebookJob(notebookId),
            ct: cancellationToken);

        _logger.LogDebug("Queued background notebook reconcile for notebook {NotebookId}", notebookId);
    }

    public Task SyncNotebookAsync(Guid notebookId) =>
        ReconcileNotebookAsync(notebookId);

    public Task SyncNotebookImmediateAsync(Guid notebookId) =>
        ReconcileNotebookImmediateAsync(notebookId);

    public Task QueueNotebookSyncAsync(Guid notebookId, CancellationToken cancellationToken = default) =>
        QueueReconcileAsync(notebookId, cancellationToken);
}
