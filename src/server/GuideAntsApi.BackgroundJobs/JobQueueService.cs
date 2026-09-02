using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Scheduling;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs;

internal static class JobQueueClaimSql
{
    // EF Core ExecuteSqlRawAsync binds positional {n} placeholders, not named @parameters.
    internal const string Claim = """
        WITH Candidate AS (
            SELECT TOP (1) j.Id
            FROM JobQueue j
            WHERE j.Status = {0}
              AND j.AvailableAt <= {1}
              AND j.ClaimToken = {2}
              AND ({3} IS NULL OR j.JobType = {3})
            ORDER BY j.Priority DESC, j.Created ASC
        )
        UPDATE j
        SET j.Status = {4},
            j.ClaimToken = {5},
            j.LeaseUntil = {6},
            j.UpdatedUtc = {1}
        FROM JobQueue j
        INNER JOIN Candidate c ON j.Id = c.Id
        WHERE j.Status = {0}
          AND j.ClaimToken = {2}
        """;
}

public class JobQueueService : IJobQueueService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IActiveJobExecutionRegistry _activeExecutionRegistry;
    private readonly JobRetryPolicy _retryPolicy;
    private readonly ILogger<JobQueueService> _logger;

    public JobQueueService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IActiveJobExecutionRegistry activeExecutionRegistry,
        IOptions<JobRetryOptions> retryOptions,
        ILogger<JobQueueService> logger)
    {
        _dbFactory = dbFactory;
        _activeExecutionRegistry = activeExecutionRegistry;
        _retryPolicy = new JobRetryPolicy(retryOptions.Value);
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
            MaxAttempts = maxAttempts ?? _retryPolicy.DefaultMaxAttempts
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
        var leaseUntil = now.AddSeconds(leaseSeconds);
        var emptyClaimToken = Guid.Empty;

        if (!context.Database.IsRelational())
        {
            return await TryClaimNonRelationalAsync(
                context,
                jobType,
                claimToken,
                leaseUntil,
                now,
                emptyClaimToken,
                ct).ConfigureAwait(false);
        }

        const string claimSql = JobQueueClaimSql.Claim;

        try
        {
            var affected = await context.Database.ExecuteSqlRawAsync(
                claimSql,
                [
                    (byte)JobStatus.Pending,
                    now,
                    emptyClaimToken,
                    (object?)jobType ?? DBNull.Value,
                    (byte)JobStatus.Processing,
                    claimToken,
                    leaseUntil,
                ],
                ct);

            if (affected == 0)
            {
                return null;
            }

            var claimedJob = await context.JobQueue
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.ClaimToken == claimToken && j.Status == JobStatus.Processing, ct);

            if (claimedJob != null)
            {
                _logger.LogDebug("Claimed job {JobType} with ID {JobId}", claimedJob.JobType, claimedJob.Id);
            }

            return claimedJob;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(
                ex,
                "Job claim failed with SqlException Number={SqlNumber} State={SqlState}",
                ex.Number,
                ex.State);
            return null;
        }
    }

    public async Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var affected = await context.JobQueue
            .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Completed)
                .SetProperty(j => j.ClaimToken, Guid.Empty)
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
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

    public async Task<bool> FailAsync(Guid id, Guid claimToken, string error, JobFailureClass failureClass = JobFailureClass.RetryableTransient, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var meta = await context.JobQueue
            .Where(j => j.Id == id)
            .Select(j => new { j.Attempts, j.MaxAttempts, j.Created })
            .FirstOrDefaultAsync(ct);

        if (meta == null) return false;

        var now = DateTime.UtcNow;

        if (failureClass == JobFailureClass.ShutdownCancellation)
        {
            var released = await context.JobQueue
                .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Pending)
                    .SetProperty(j => j.ClaimToken, Guid.Empty)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                    .SetProperty(j => j.AvailableAt, now)
                    .SetProperty(j => j.UpdatedUtc, now), ct);

            if (released > 0)
            {
                _logger.LogInformation(
                    "Released job {JobId} back to pending after shutdown cancellation without burning attempt budget",
                    id);
            }

            return released > 0;
        }

        var burnsAttemptBudget = JobRetryPolicy.BurnsAttemptBudget(failureClass);
        var attemptsNext = burnsAttemptBudget ? meta.Attempts + 1 : meta.Attempts;
        var plan = _retryPolicy.PlanFailure(failureClass, meta.Attempts, meta.MaxAttempts, meta.Created, now);
        var willRetry = plan.WillRetry;
        var nextAvailable = plan.NextAvailableAt;

        int affected;
        if (willRetry)
        {
            var update = context.JobQueue
                .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing);

            if (burnsAttemptBudget)
            {
                affected = await update.ExecuteUpdateAsync(s => s
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
                affected = await update.ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.ErrorMessage, error)
                    .SetProperty(j => j.UpdatedUtc, now)
                    .SetProperty(j => j.Status, JobStatus.Pending)
                    .SetProperty(j => j.AvailableAt, nextAvailable!.Value)
                    .SetProperty(j => j.ClaimToken, Guid.Empty)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            }
        }
        else
        {
            var update = context.JobQueue
                .Where(j => j.Id == id && j.ClaimToken == claimToken && j.Status == JobStatus.Processing);

            if (burnsAttemptBudget)
            {
                affected = await update.ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.ErrorMessage, error)
                    .SetProperty(j => j.UpdatedUtc, now)
                    .SetProperty(j => j.Status, JobStatus.Failed)
                    .SetProperty(j => j.ClaimToken, Guid.Empty)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            }
            else
            {
                affected = await update.ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.ErrorMessage, error)
                    .SetProperty(j => j.UpdatedUtc, now)
                    .SetProperty(j => j.Status, JobStatus.Failed)
                    .SetProperty(j => j.ClaimToken, Guid.Empty)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            }
        }

        var success = affected > 0;
        if (success)
        {
            if (willRetry)
            {
                _logger.LogWarning("Job {JobId} failed (attempt {Attempts}/{MaxAttempts}), will retry at {RetryAt}: {Error}", 
                    id, attemptsNext, meta.MaxAttempts, nextAvailable, LogValueSanitizer.Sanitize(error));
            }
            else
            {
                _logger.LogError("Job {JobId} permanently failed after {Attempts} attempts ({FailureClass}): {Error}", 
                    id, attemptsNext, failureClass, LogValueSanitizer.Sanitize(error));
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
                j.Created,
                j.LeaseUntil
            })
            .ToListAsync(ct);

        var reclaimable = expiredCandidates
            .Where(j => j.LeaseUntil < reclaimCutoff)
            .Where(j => !locallyActive.Contains(new ActiveJobExecutionKey(j.Id, j.ClaimToken)))
            .ToList();

        var requeued = 0;
        var failed = 0;

        foreach (var candidate in reclaimable)
        {
            var delay = _retryPolicy.ComputeDelay(candidate.Attempts);
            var canRetry = _retryPolicy.CanRetry(
                JobFailureClass.LeaseOwnershipLost,
                candidate.Attempts,
                candidate.MaxAttempts,
                candidate.Created,
                now,
                delay);

            if (!canRetry)
            {
                failed += await context.JobQueue
                    .Where(j => j.Id == candidate.Id && j.ClaimToken == candidate.ClaimToken && j.Status == JobStatus.Processing)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, JobStatus.Failed)
                        .SetProperty(j => j.ErrorMessage, j => $"Job lease expired and max attempts reached at {now:O}")
                        .SetProperty(j => j.ClaimToken, Guid.Empty)
                        .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                        .SetProperty(j => j.UpdatedUtc, now), ct);
            }
            else
            {
                var nextAvailable = now.Add(delay);
                requeued += await context.JobQueue
                    .Where(j => j.Id == candidate.Id && j.ClaimToken == candidate.ClaimToken && j.Status == JobStatus.Processing)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.ErrorMessage, j => $"Job lease expired at {now:O}; requeued for retry (lease ownership lost)")
                        .SetProperty(j => j.Status, JobStatus.Pending)
                        .SetProperty(j => j.ClaimToken, Guid.Empty)
                        .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                        .SetProperty(j => j.AvailableAt, nextAvailable)
                        .SetProperty(j => j.UpdatedUtc, now), ct);
            }
        }

        var skippedActive = expiredCandidates.Count - reclaimable.Count;

        if (requeued > 0)
        {
            _logger.LogInformation("Requeued {Count} expired jobs due to lease ownership loss", requeued);
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

        var scheduledInterrupted = await context.JobQueue
            .Where(j => j.Status == JobStatus.Processing && j.JobType == ProjectScheduledJobExecutionJob.JobType)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.ClaimToken, Guid.Empty)
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                .SetProperty(j => j.ErrorMessage, "Interrupted by process restart; scheduled jobs are not automatically retried.")
                .SetProperty(j => j.UpdatedUtc, now), ct);

        if (scheduledInterrupted > 0)
        {
            _logger.LogInformation(
                "Marked {Count} interrupted scheduled job queue items as failed on startup",
                scheduledInterrupted);
        }

        var affected = await context.JobQueue
            .Where(j => j.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.ClaimToken, Guid.Empty)
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                .SetProperty(j => j.UpdatedUtc, now), ct);

        // Queue items are reconciled before scheduled-job runs. If the scheduler ran first on
        // startup it may have skipped failing runs that still had Processing queue rows.
        await ProjectScheduledJobInFlightGuard.FailOrphanedRunningRunsAsync(context, ct);

        // Stream locks are process-owned. A restart means the holder is gone, so clear them
        // immediately — do not wait for ExpiresAt, or the local-AI job gate stays closed.
        await ConversationLockRestartReconciliation.ClearAllLocksAsync(context, _logger, ct);

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

    private static async Task<JobQueue?> TryClaimNonRelationalAsync(
        ApplicationDbContext context,
        string? jobType,
        Guid claimToken,
        DateTime leaseUntil,
        DateTime now,
        Guid emptyClaimToken,
        CancellationToken ct)
    {
        var candidate = await context.JobQueue
            .Where(j =>
                j.Status == JobStatus.Pending
                && j.AvailableAt <= now
                && j.ClaimToken == emptyClaimToken
                && (jobType == null || j.JobType == jobType))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.Created)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (candidate == null)
        {
            return null;
        }

        var affected = await context.JobQueue
            .Where(j =>
                j.Id == candidate.Id
                && j.Status == JobStatus.Pending
                && j.ClaimToken == emptyClaimToken)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Status, JobStatus.Processing)
                    .SetProperty(j => j.ClaimToken, claimToken)
                    .SetProperty(j => j.LeaseUntil, leaseUntil)
                    .SetProperty(j => j.UpdatedUtc, now),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return null;
        }

        return await context.JobQueue
            .AsNoTracking()
            .FirstOrDefaultAsync(
                j => j.ClaimToken == claimToken && j.Status == JobStatus.Processing,
                ct)
            .ConfigureAwait(false);
    }
}
