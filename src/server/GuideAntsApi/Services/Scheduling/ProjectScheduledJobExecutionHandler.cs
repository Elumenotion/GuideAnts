using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Scheduling;

public sealed class ProjectScheduledJobExecutionHandler : JobHandlerBase<ProjectScheduledJobExecutionJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;

    public ProjectScheduledJobExecutionHandler(
        ILogger<ProjectScheduledJobExecutionHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IServiceScopeFactory scopeFactory) : base(logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
    }

    public override string JobType => ProjectScheduledJobExecutionJob.JobType;

    public override async Task<bool> HandleAsync(ProjectScheduledJobExecutionJob payload, CancellationToken cancellationToken)
    {
        // Job handlers are resolved once at startup and cached for the process lifetime, so this
        // handler must never hold scoped services (e.g. IConversationService and its ApplicationDbContext)
        // as fields. Create a fresh DI scope per invocation and resolve scoped dependencies from it.
        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IProjectScheduledJobExecutor>();
        var cronScheduleService = scope.ServiceProvider.GetRequiredService<ICronScheduleService>();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var job = await db.ProjectScheduledJobs
            .FirstOrDefaultAsync(j => j.Id == payload.ScheduledJobId, cancellationToken);

        if (job == null)
        {
            Logger.LogWarning("Scheduled job {JobId} no longer exists", payload.ScheduledJobId);
            return true;
        }

        if (!Enum.TryParse<ProjectScheduledJobTrigger>(payload.TriggeredBy, ignoreCase: true, out var trigger))
        {
            trigger = ProjectScheduledJobTrigger.Schedule;
        }

        var gate = await ProjectScheduledJobInFlightGuard.ReconcileBeforeExecutionAsync(db, job.Id, cancellationToken);
        if (gate == ProjectScheduledJobExecutionGate.SkipDuplicate)
        {
            Logger.LogWarning(
                "Skipping duplicate scheduled job execution for {JobId}; another run is already in progress",
                job.Id);
            return true;
        }

        var run = new ProjectScheduledJobRun
        {
            ScheduledJobId = job.Id,
            TriggeredBy = trigger,
            StartedUtc = DateTime.UtcNow,
            Status = ProjectScheduledJobRunStatus.Running
        };

        db.ProjectScheduledJobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        ProjectScheduledJobExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(job, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled error executing scheduled job {JobId}", job.Id);
            result = new ProjectScheduledJobExecutionResult(false, ex.Message, null, null, null, null);
        }

        ApplyRunResult(run, result);
        await db.SaveChangesAsync(cancellationToken);

        await PersistJobSummaryAsync(db, job, run, cronScheduleService, cancellationToken);
        return true;
    }

    private static void ApplyRunResult(ProjectScheduledJobRun run, ProjectScheduledJobExecutionResult result)
    {
        run.CompletedUtc = DateTime.UtcNow;
        run.Status = result.Succeeded
            ? ProjectScheduledJobRunStatus.Succeeded
            : ProjectScheduledJobRunStatus.Failed;
        run.ErrorMessage = ScheduledJobOutputTruncator.TruncateErrorMessage(result.ErrorMessage);
        run.StandardOutput = result.StandardOutput;
        run.StandardError = result.StandardError;
        run.CreatedConversationId = result.CreatedConversationId;
        run.ExitCode = result.ExitCode;
    }

    private async Task PersistJobSummaryAsync(
        ApplicationDbContext db,
        ProjectScheduledJob job,
        ProjectScheduledJobRun run,
        ICronScheduleService cronScheduleService,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.Entry(job).ReloadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to reload scheduled job {JobId} before summary update; run {RunId} was persisted",
                job.Id,
                run.Id);
            return;
        }

        var completedUtc = run.CompletedUtc ?? DateTime.UtcNow;
        job.LastRunUtc = completedUtc;
        job.LastRunStatus = run.Status == ProjectScheduledJobRunStatus.Succeeded
            ? ProjectScheduledJobLastRunStatus.Succeeded
            : ProjectScheduledJobLastRunStatus.Failed;
        job.UpdatedUtc = DateTime.UtcNow;

        if (job.IsEnabled && (job.NextRunUtc == null || job.NextRunUtc <= completedUtc))
        {
            job.NextRunUtc = cronScheduleService.GetNextOccurrenceUtc(
                job.CronExpression,
                job.TimeZoneId,
                completedUtc);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogWarning(
                ex,
                "Concurrency conflict updating scheduled job {JobId} summary after run {RunId}; run result was persisted",
                job.Id,
                run.Id);
        }
    }
}
