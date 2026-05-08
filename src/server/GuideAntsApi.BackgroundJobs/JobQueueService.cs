using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs;

public class JobQueueService : IJobQueueService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IActiveJobExecutionRegistry _activeExecutionRegistry;
    private readonly ILogger<JobQueueService> _logger;

    public JobQueueService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IActiveJobExecutionRegistry activeExecutionRegistry,
        ILogger<JobQueueService> logger)
    {
        _dbFactory = dbFactory;
        _activeExecutionRegistry = activeExecutionRegistry;
        _logger = logger;
    }

    public async Task<Guid> EnqueueAsync(string jobType, object payload, int priority = 0, DateTime? availableAt = null, Guid? correlationId = null, int? maxAttempts = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var job = new JobQueue
        {
            JobType = jobType,
            PayloadJson = JsonSerializer.Serialize(payload),
            Priority = priority,
            AvailableAt = availableAt ?? DateTime.UtcNow,
            CorrelationId = correlationId,
            MaxAttempts = maxAttempts ?? 5
        };

        context.JobQueue.Add(job);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Enqueued job {JobType} with ID {JobId}, Priority {Priority}", 
            jobType, job.Id, priority);

        return job.Id;
    }

    public async Task<JobQueue?> TryClaimAsync(string? jobType, int leaseSeconds, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var claimToken = Guid.NewGuid();

        // Build query for available jobs
        var query = context.JobQueue
            .Where(j => j.Status == JobStatus.Pending 
                       && j.AvailableAt <= now 
                       && j.ClaimToken == Guid.Empty); // Unclaimed jobs only
                       
        if (!string.IsNullOrEmpty(jobType))
            query = query.Where(j => j.JobType == jobType);

        // Atomic claim operation - only one worker can succeed due to optimistic concurrency
        var affected = await query
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.Created)
            .Take(1) // Critical: only claim one job
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Processing)
                .SetProperty(j => j.ClaimToken, claimToken)
                .SetProperty(j => j.LeaseUntil, now.AddSeconds(leaseSeconds))
                .SetProperty(j => j.UpdatedUtc, now), ct);

        if (affected == 0) return null; // No available jobs or lost race

        // Retrieve the claimed job
        var claimedJob = await context.JobQueue
            .AsNoTracking() // Read-only
            .FirstOrDefaultAsync(j => j.ClaimToken == claimToken && j.Status == JobStatus.Processing, ct);

        if (claimedJob != null)
        {
            _logger.LogDebug("Claimed job {JobType} with ID {JobId}", claimedJob.JobType, claimedJob.Id);
        }

        return claimedJob;
    }

    public async Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var affected = await context.JobQueue
            .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Completed)
                .SetProperty(j => j.UpdatedUtc, now), ct);

        var success = affected > 0;
        if (success)
        {
            _logger.LogInformation("Completed job with ID {JobId}", id);
        }
        else
        {
            _logger.LogWarning("Failed to complete job with ID {JobId} - job not found or claim token mismatch", id);
        }

        return success;
    }

    public async Task<bool> FailAsync(Guid id, Guid claimToken, string error, int baseDelaySeconds = 10, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // Get current job metadata for retry logic
        var meta = await context.JobQueue
            .Where(j => j.Id == id)
            .Select(j => new { j.Attempts, j.MaxAttempts })
            .FirstOrDefaultAsync(ct);

        if (meta == null) return false;

        var attemptsNext = meta.Attempts + 1;
        var willRetry = attemptsNext < meta.MaxAttempts;
        var now = DateTime.UtcNow;
        var nextAvailable = willRetry ? now.AddSeconds(Math.Pow(2, meta.Attempts) * baseDelaySeconds) : (DateTime?)null;

        int affected;
        if (willRetry)
        {
            affected = await context.JobQueue
                .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.ErrorMessage, error)
                    .SetProperty(j => j.UpdatedUtc, now)
                    .SetProperty(j => j.Status, JobStatus.Pending)
                    .SetProperty(j => j.AvailableAt, nextAvailable!.Value)
                    .SetProperty(j => j.ClaimToken, Guid.Empty)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
        }
        else
        {
            affected = await context.JobQueue
                .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.ErrorMessage, error)
                    .SetProperty(j => j.UpdatedUtc, now)
                    .SetProperty(j => j.Status, JobStatus.Failed), ct);
        }

        var success = affected > 0;
        if (success)
        {
            if (willRetry)
            {
                _logger.LogWarning("Job {JobId} failed (attempt {Attempts}/{MaxAttempts}), will retry at {RetryAt}: {Error}", 
                    id, attemptsNext, meta.MaxAttempts, nextAvailable, error);
            }
            else
            {
                _logger.LogError("Job {JobId} permanently failed after {Attempts} attempts: {Error}", 
                    id, attemptsNext, error);
            }
        }

        return success;
    }

    public async Task<int> RequeueExpiredAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var reclaimGrace = TimeSpan.FromSeconds(30);
        var reclaimCutoff = now.Subtract(reclaimGrace);
        var recentActiveCutoff = now.Subtract(TimeSpan.FromSeconds(120));
        var locallyActive = _activeExecutionRegistry.GetActiveSince(recentActiveCutoff);

        var expiredCandidates = await context.JobQueue
            .Where(j => j.Status == JobStatus.Processing && j.LeaseUntil != null && j.LeaseUntil < now)
            .Where(j => j.ClaimToken != Guid.Empty)
            .Select(j => new
            {
                j.Id,
                j.ClaimToken,
                j.Attempts,
                j.MaxAttempts,
                j.LeaseUntil
            })
            .ToListAsync(ct);

        var reclaimable = expiredCandidates
            .Where(j => j.LeaseUntil < reclaimCutoff)
            .Where(j => !locallyActive.Contains(new ActiveJobExecutionKey(j.Id, j.ClaimToken)))
            .ToList();

        var requeued = 0;
        var failed = 0;

        // Expired lease counts as an attempt so long-running jobs cannot churn forever.
        foreach (var candidate in reclaimable)
        {
            if (candidate.Attempts + 1 >= candidate.MaxAttempts)
            {
                failed += await context.JobQueue
                    .Where(j => j.Id == candidate.Id && j.ClaimToken == candidate.ClaimToken && j.Status == JobStatus.Processing)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                        .SetProperty(j => j.Status, JobStatus.Failed)
                        .SetProperty(j => j.ErrorMessage, j => $"Job lease expired and max attempts reached at {now:O}")
                        .SetProperty(j => j.ClaimToken, Guid.Empty)
                        .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                        .SetProperty(j => j.UpdatedUtc, now), ct);
            }
            else
            {
                requeued += await context.JobQueue
                    .Where(j => j.Id == candidate.Id && j.ClaimToken == candidate.ClaimToken && j.Status == JobStatus.Processing)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                        .SetProperty(j => j.ErrorMessage, j => $"Job lease expired at {now:O}; requeued for retry")
                        .SetProperty(j => j.Status, JobStatus.Pending)
                        .SetProperty(j => j.ClaimToken, Guid.Empty) // Reset to unclaimed
                        .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                        .SetProperty(j => j.AvailableAt, now)
                        .SetProperty(j => j.UpdatedUtc, now), ct);
            }
        }

        var skippedActive = expiredCandidates.Count - reclaimable.Count;

        if (requeued > 0)
        {
            _logger.LogInformation("Requeued {Count} expired jobs", requeued);
        }

        if (failed > 0)
        {
            _logger.LogWarning("Marked {Count} expired jobs as failed after reaching max attempts", failed);
        }

        if (skippedActive > 0)
        {
            _logger.LogDebug(
                "Skipped lease cleanup for {Count} locally-active expired jobs (multi-instance-safe first defense).",
                skippedActive);
        }

        return requeued + failed;
    }

    public async Task<int> RequeueAllProcessingAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var affected = await context.JobQueue
            .Where(j => j.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.ClaimToken, Guid.Empty) // Reset to unclaimed
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                .SetProperty(j => j.UpdatedUtc, now), ct);

        return affected;
    }

    public async Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var affected = await context.JobQueue
            .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseUntil, j => j.LeaseUntil!.Value.AddSeconds(additionalSeconds))
                .SetProperty(j => j.UpdatedUtc, now), ct);

        var success = affected > 0;
        if (success)
        {
            _logger.LogDebug("Renewed lease for job {JobId} by {AdditionalSeconds} seconds", id, additionalSeconds);
        }

        return success;
    }
}
