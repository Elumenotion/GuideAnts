using GuideAntsApi.BackgroundJobs.Sync;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class SyncNotebookHandler : JobHandlerBase<SyncNotebookJob>
{
    public const string LockNotAcquiredSkipReason = "LockNotAcquired";

    private readonly INotebookFileReconciler _reconciler;

    public SyncNotebookHandler(
        ILogger<SyncNotebookHandler> logger,
        INotebookFileReconciler reconciler) : base(logger)
    {
        _reconciler = reconciler;
    }

    public override string JobType => nameof(SyncNotebookJob).Replace("Job", string.Empty);

    public override async Task<JobExecutionResult> HandleAsync(SyncNotebookJob payload, CancellationToken cancellationToken)
    {
        var result = await _reconciler.ReconcileNotebookAsync(payload.NotebookId, ReconcileMode.Full, cancellationToken);
        if (result.Skipped && result.SkipReason == LockNotAcquiredSkipReason)
        {
            return JobExecutionResult.RetryableTransient(
                "Notebook reconcile skipped because another sync holds the lock");
        }

        return JobExecutionResult.Success();
    }
}
