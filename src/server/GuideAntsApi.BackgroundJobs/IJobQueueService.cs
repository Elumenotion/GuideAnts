using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs;

public interface IJobQueueService
{
    /// <summary>
    /// Enqueue a new job for background processing
    /// </summary>
    Task<Guid> EnqueueAsync(string jobType, object payload, int priority = 0, DateTime? availableAt = null, Guid? correlationId = null, int? maxAttempts = null, CancellationToken ct = default);
    
    /// <summary>
    /// Try to claim the next available job of the specified type
    /// </summary>
    Task<JobQueue?> TryClaimAsync(string? jobType, int leaseSeconds, CancellationToken ct = default);
    
    /// <summary>
    /// Mark a job as completed
    /// </summary>
    Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default);
    
    /// <summary>
    /// Mark a job as failed and schedule retry with exponential backoff
    /// </summary>
    Task<bool> FailAsync(Guid id, Guid claimToken, string error, int baseDelaySeconds = 10, CancellationToken ct = default);
    
    /// <summary>
    /// Requeue jobs with expired leases
    /// </summary>
    Task<int> RequeueExpiredAsync(CancellationToken ct = default);

    /// <summary>
    /// Requeue all processing jobs regardless of lease (startup recovery).
    /// </summary>
    Task<int> RequeueAllProcessingAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Renew the lease for a long-running job
    /// </summary>
    Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default);
}

