using System.Text.Json;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.BackgroundJobs.Scheduling;

public enum ProjectScheduledJobExecutionGate
{
    Proceed,
    SkipDuplicate
}

public static class ProjectScheduledJobInFlightGuard
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task FailOrphanedRunningRunsAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var runningRuns = await db.ProjectScheduledJobRuns
            .Where(r => r.Status == ProjectScheduledJobRunStatus.Running)
            .ToListAsync(cancellationToken);

        if (runningRuns.Count == 0)
        {
            return;
        }

        var processingJobIds = await GetScheduledJobIdsWithQueueStatusAsync(
            db,
            [JobStatus.Processing],
            cancellationToken);

        var now = DateTime.UtcNow;
        var failedAny = false;
        foreach (var run in runningRuns)
        {
            if (processingJobIds.Contains(run.ScheduledJobId))
            {
                continue;
            }

            run.Status = ProjectScheduledJobRunStatus.Failed;
            run.CompletedUtc = now;
            run.ErrorMessage = ScheduledJobOutputTruncator.TruncateErrorMessage(
                "Run interrupted by process restart.");
            failedAny = true;
        }

        if (failedAny)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public static async Task<ProjectScheduledJobExecutionGate> ReconcileBeforeExecutionAsync(
        ApplicationDbContext db,
        Guid scheduledJobId,
        CancellationToken cancellationToken)
    {
        var hasRunningRun = await db.ProjectScheduledJobRuns
            .AnyAsync(
                r => r.ScheduledJobId == scheduledJobId && r.Status == ProjectScheduledJobRunStatus.Running,
                cancellationToken);
        if (!hasRunningRun)
        {
            return ProjectScheduledJobExecutionGate.Proceed;
        }

        if (await HasProcessingQueueItemAsync(db, scheduledJobId, cancellationToken))
        {
            return ProjectScheduledJobExecutionGate.SkipDuplicate;
        }

        var now = DateTime.UtcNow;
        var orphanedRuns = await db.ProjectScheduledJobRuns
            .Where(r => r.ScheduledJobId == scheduledJobId && r.Status == ProjectScheduledJobRunStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var run in orphanedRuns)
        {
            run.Status = ProjectScheduledJobRunStatus.Failed;
            run.CompletedUtc = now;
            run.ErrorMessage = ScheduledJobOutputTruncator.TruncateErrorMessage(
                "Run interrupted by process restart.");
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProjectScheduledJobExecutionGate.Proceed;
    }

    public static Task<bool> HasInFlightQueueItemAsync(
        ApplicationDbContext db,
        Guid scheduledJobId,
        CancellationToken cancellationToken) =>
        HasQueueItemAsync(
            db,
            scheduledJobId,
            [JobStatus.Pending, JobStatus.Processing],
            cancellationToken);

    private static Task<bool> HasProcessingQueueItemAsync(
        ApplicationDbContext db,
        Guid scheduledJobId,
        CancellationToken cancellationToken) =>
        HasQueueItemAsync(
            db,
            scheduledJobId,
            [JobStatus.Processing],
            cancellationToken);

    private static async Task<bool> HasQueueItemAsync(
        ApplicationDbContext db,
        Guid scheduledJobId,
        IReadOnlyCollection<JobStatus> statuses,
        CancellationToken cancellationToken)
    {
        var scheduledJobIds = await GetScheduledJobIdsWithQueueStatusAsync(db, statuses, cancellationToken);
        return scheduledJobIds.Contains(scheduledJobId);
    }

    private static async Task<HashSet<Guid>> GetScheduledJobIdsWithQueueStatusAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<JobStatus> statuses,
        CancellationToken cancellationToken)
    {
        var payloads = await db.JobQueue.AsNoTracking()
            .Where(q => q.JobType == ProjectScheduledJobExecutionJob.JobType && statuses.Contains(q.Status))
            .Select(q => q.PayloadJson)
            .ToListAsync(cancellationToken);

        var scheduledJobIds = new HashSet<Guid>();
        foreach (var payloadJson in payloads)
        {
            var payload = TryDeserializePayload(payloadJson);
            if (payload != null)
            {
                scheduledJobIds.Add(payload.ScheduledJobId);
            }
        }

        return scheduledJobIds;
    }

    public static ProjectScheduledJobExecutionJob? TryDeserializePayload(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectScheduledJobExecutionJob>(payloadJson, PayloadJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
