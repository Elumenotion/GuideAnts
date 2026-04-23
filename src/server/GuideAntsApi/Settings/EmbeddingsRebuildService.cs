using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Settings;

public interface IEmbeddingsRebuildService
{
    Task<(bool Enqueued, Guid JobId, string Status)> RequestRebuildAsync(CancellationToken cancellationToken = default);
}

public sealed class EmbeddingsRebuildService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IJobQueueService jobQueue) : IEmbeddingsRebuildService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory = dbFactory;
    private readonly IJobQueueService _jobQueue = jobQueue;

    public async Task<(bool Enqueued, Guid JobId, string Status)> RequestRebuildAsync(CancellationToken cancellationToken = default)
    {
        var rebuildJobType = nameof(RebuildEmbeddingsJob).Replace("Job", string.Empty);

        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existingJob = await context.JobQueue
            .AsNoTracking()
            .Where(j => j.JobType == rebuildJobType && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing))
            .OrderBy(j => j.Created)
            .Select(j => new { j.Id, j.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (existingJob != null)
        {
            return (false, existingJob.Id, existingJob.Status.ToString().ToLowerInvariant());
        }

        var jobId = await _jobQueue.EnqueueAsync(
            jobType: rebuildJobType,
            payload: new RebuildEmbeddingsJob(),
            ct: cancellationToken);

        return (true, jobId, "queued");
    }
}
