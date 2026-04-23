using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.BackgroundJobs.Services;

/// <summary>
/// Lightweight scheduler that enqueues retention cleanup jobs for published guides
/// immediately on startup and then once per hour. Processes guides with RetentionPeriod configured.
/// </summary>
public class RetentionCleanupScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RetentionCleanupOptions _opts;
    private readonly ILogger<RetentionCleanupScheduler> _log;

    public RetentionCleanupScheduler(
        IServiceScopeFactory scopeFactory,
        IOptions<RetentionCleanupOptions> options,
        ILogger<RetentionCleanupScheduler> log)
    {
        _scopeFactory = scopeFactory;
        _opts = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Retention cleanup scheduler disabled");
            return;
        }

        // Smoke-test behavior: enqueue immediately on startup so restarts trigger a cleanup pass.
        await EnqueueRetentionCleanupJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            
            // Calculate next top of hour
            var nextRun = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc)
                .AddHours(1);

            var delay = nextRun - nowUtc;
            _log.LogInformation("Retention cleanup scheduler sleeping {Delay} until {NextRun}", delay, nextRun);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await EnqueueRetentionCleanupJobsAsync(stoppingToken);
        }
    }

    private async Task EnqueueRetentionCleanupJobsAsync(CancellationToken stoppingToken)
    {
        // Enqueue one job per active guide with RetentionPeriod
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueueService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
        var guideIds = await db.PublishedGuides
            .Where(g => g.RetentionPeriod != null && g.Active)
            .Select(g => g.Id)
            .ToListAsync(stoppingToken);

        foreach (var guideId in guideIds)
        {
            try
            {
                await queue.EnqueueAsync(
                    jobType: nameof(RetentionCleanupJob).Replace("Job", string.Empty),
                    payload: new RetentionCleanupJob(guideId),
                    ct: stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to enqueue retention cleanup job for guide {GuideId}", guideId);
            }
        }

        _log.LogInformation("Enqueued {Count} retention cleanup jobs", guideIds.Count);
    }
}




