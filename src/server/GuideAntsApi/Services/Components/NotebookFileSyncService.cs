using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components.Sync;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Components;

public class NotebookFileSyncService : INotebookFileSyncService
{
    private static readonly string SyncNotebookJobType =
        nameof(SyncNotebookJob).Replace("Job", string.Empty);

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
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var alreadyQueued = await context.JobQueue.AnyAsync(
            j => j.CorrelationId == notebookId
                 && j.JobType == SyncNotebookJobType
                 && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing),
            cancellationToken);

        if (alreadyQueued)
        {
            _logger.LogDebug(
                "Skipping duplicate SyncNotebook enqueue for notebook {NotebookId}",
                notebookId);
            return;
        }

        try
        {
            await jobQueue.EnqueueAsync(
                jobType: SyncNotebookJobType,
                payload: new SyncNotebookJob(notebookId),
                correlationId: notebookId,
                ct: cancellationToken);
        }
        catch (DbUpdateException ex) when (IsCorrelationDedupViolation(ex))
        {
            // The pre-check is only an optimization. Concurrent requests can all pass it;
            // the unique filtered index is the atomic deduplication boundary.
            _logger.LogDebug(
                "Skipping duplicate SyncNotebook enqueue after concurrent insert for notebook {NotebookId}",
                notebookId);
            return;
        }

        _logger.LogDebug("Queued background notebook reconcile for notebook {NotebookId}", notebookId);
    }

    public Task SyncNotebookAsync(Guid notebookId) =>
        ReconcileNotebookAsync(notebookId);

    public Task SyncNotebookImmediateAsync(Guid notebookId) =>
        ReconcileNotebookImmediateAsync(notebookId);

    public Task QueueNotebookSyncAsync(Guid notebookId, CancellationToken cancellationToken = default) =>
        QueueReconcileAsync(notebookId, cancellationToken);

    private static bool IsCorrelationDedupViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current != null; current = current.InnerException)
        {
            if (current.Message.Contains("IX_JobQueue_CorrelationDedup", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
