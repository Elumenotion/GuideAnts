using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Scheduling;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Scheduling;

public sealed class ProjectScheduledJobScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProjectScheduledJobOptions _opts;
    private readonly ILogger<ProjectScheduledJobScheduler> _log;

    public ProjectScheduledJobScheduler(
        IServiceScopeFactory scopeFactory,
        IOptions<ProjectScheduledJobOptions> options,
        ILogger<ProjectScheduledJobScheduler> log)
    {
        _scopeFactory = scopeFactory;
        _opts = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Project scheduled job scheduler disabled");
            return;
        }

        await FailOrphanedRunningRunsAsync(stoppingToken);
        await RecomputeNextRunsAsync(stoppingToken);
        await ProcessDueJobsAsync(stoppingToken);

        var pollInterval = TimeSpan.FromSeconds(Math.Max(15, _opts.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessDueJobsAsync(stoppingToken);
        }
    }

    private async Task FailOrphanedRunningRunsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
        await ProjectScheduledJobInFlightGuard.FailOrphanedRunningRunsAsync(db, stoppingToken);
        _log.LogInformation("Reconciled orphaned scheduled job runs after startup");
    }

    private async Task RecomputeNextRunsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var cron = scope.ServiceProvider.GetRequiredService<ICronScheduleService>();

        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
        var jobs = await db.ProjectScheduledJobs
            .Where(j => j.IsEnabled)
            .ToListAsync(stoppingToken);

        var now = DateTime.UtcNow;
        foreach (var job in jobs)
        {
            if (job.NextRunUtc != null && job.NextRunUtc <= now)
            {
                // Leave past-due next run in place so ProcessDueJobsAsync can enqueue a catch-up run.
                continue;
            }

            job.NextRunUtc = cron.GetNextOccurrenceUtc(job.CronExpression, job.TimeZoneId, now);
            job.UpdatedUtc = now;
        }

        if (jobs.Count > 0)
        {
            await db.SaveChangesAsync(stoppingToken);
            _log.LogInformation("Recomputed next run times for {Count} scheduled jobs", jobs.Count);
        }
    }

    private async Task ProcessDueJobsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueueService>();
        var cron = scope.ServiceProvider.GetRequiredService<ICronScheduleService>();

        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
        var now = DateTime.UtcNow;

        var dueJobs = await db.ProjectScheduledJobs
            .Where(j => j.IsEnabled && j.NextRunUtc != null && j.NextRunUtc <= now)
            .OrderBy(j => j.NextRunUtc)
            .Take(20)
            .ToListAsync(stoppingToken);

        foreach (var job in dueJobs)
        {
            var hasRunningRun = await db.ProjectScheduledJobRuns
                .AnyAsync(r => r.ScheduledJobId == job.Id && r.Status == ProjectScheduledJobRunStatus.Running, stoppingToken);
            if (hasRunningRun)
            {
                _log.LogDebug("Skipping scheduled job {JobId} because a run is already in progress", job.Id);
                continue;
            }

            if (await ProjectScheduledJobInFlightGuard.HasInFlightQueueItemAsync(db, job.Id, stoppingToken))
            {
                _log.LogDebug(
                    "Skipping scheduled job {JobId} because a queue item is already pending or processing",
                    job.Id);
                continue;
            }

            job.NextRunUtc = cron.GetNextOccurrenceUtc(job.CronExpression, job.TimeZoneId, now);
            job.UpdatedUtc = now;

            try
            {
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }

            try
            {
                await queue.EnqueueAsync(
                    jobType: ProjectScheduledJobExecutionJob.JobType,
                    payload: new ProjectScheduledJobExecutionJob(job.Id, ProjectScheduledJobTrigger.Schedule.ToString()),
                    ct: stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to enqueue scheduled job {JobId}", job.Id);
            }
        }
    }
}
