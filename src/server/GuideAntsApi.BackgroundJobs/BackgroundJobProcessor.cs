using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs;

public class BackgroundJobProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly JobProcessorOptions _options;
    private readonly ILogger<BackgroundJobProcessor> _logger;
    private readonly IActiveJobExecutionRegistry _activeExecutionRegistry;
    private readonly Dictionary<string, SemaphoreSlim> _concurrencyLimits;
    private readonly Dictionary<string, IJobHandler> _jobHandlers;
    private readonly HashSet<string> _lockGatedJobTypes;
    private readonly AdaptiveLoopBackoff _loopBackoff;
    private DateTime _lastLockGateLogUtc = DateTime.MinValue;

    public BackgroundJobProcessor(
        IServiceProvider serviceProvider,
        IOptions<JobProcessorOptions> options,
        IOptions<JobRetryOptions> retryOptions,
        IActiveJobExecutionRegistry activeExecutionRegistry,
        ILogger<BackgroundJobProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _activeExecutionRegistry = activeExecutionRegistry;
        _logger = logger;
        _loopBackoff = new AdaptiveLoopBackoff(retryOptions.Value.LoopBackoffSeconds);
        
        // Initialize concurrency limits for each job type
        _concurrencyLimits = new Dictionary<string, SemaphoreSlim>();
        foreach (var (jobType, jobOptions) in _options.JobTypes)
        {
            _concurrencyLimits[jobType] = new SemaphoreSlim(jobOptions.MaxConcurrency, jobOptions.MaxConcurrency);
        }

        _lockGatedJobTypes = _options.ConversationLockGate.Enabled
            ? new HashSet<string>(_options.ConversationLockGate.GatedJobTypes, StringComparer.Ordinal)
            : [];

        // Initialize job handlers dictionary
        _jobHandlers = new Dictionary<string, IJobHandler>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background job processor started with {JobTypeCount} job types configured", 
            _options.JobTypes.Count);

        // Initialize job handlers on startup
        await InitializeJobHandlersAsync();

        if (_options.RequeueProcessingOnStartup)
        {
            await RequeueProcessingJobsOnStartupAsync(stoppingToken);
        }

        // Small startup jitter to avoid thundering herd across replicas
        var rng = new Random();
        var initialJitterMs = rng.Next(250, 1500);
        await Task.Delay(initialJitterMs, stoppingToken);

        // Non-blocking loop - runs on background thread pool
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAvailableJobsAsync(stoppingToken);
                await CleanupExpiredLeasesAsync(stoppingToken);
                _loopBackoff.Reset();
                
                // Short delay to prevent tight loop, add small jitter per-iteration to reduce herd effects
                var pollSeconds = Math.Max(1, _options.PollingIntervalSeconds);
                var jitterMs = rng.Next(100, 400);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds).Add(TimeSpan.FromMilliseconds(jitterMs)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Expected during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background job processing");
                await Task.Delay(_loopBackoff.NextDelayWithJitter(rng), stoppingToken);
            }
        }

        _logger.LogInformation("Background job processor stopped");
    }

    private Task InitializeJobHandlersAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>();
        
        foreach (var handler in handlers)
        {
            _jobHandlers[handler.JobType] = handler;
            _logger.LogDebug("Registered job handler for type: {JobType}", handler.JobType);
        }

        var unconfiguredHandlers = _jobHandlers.Keys
            .Where(jobType => !_options.JobTypes.ContainsKey(jobType))
            .OrderBy(jobType => jobType, StringComparer.Ordinal)
            .ToList();

        if (unconfiguredHandlers.Count > 0)
        {
            throw new InvalidOperationException(
                $"Background job handler(s) registered without matching BackgroundJobs:JobTypes configuration: {string.Join(", ", unconfiguredHandlers)}");
        }

        _logger.LogInformation("Initialized {HandlerCount} job handlers", _jobHandlers.Count);
        return Task.CompletedTask;
    }

    private async Task ProcessAvailableJobsAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();
        bool? hasActiveConversationLock = null;
        bool? bothChatAndEmbeddingsUseLocalAi = null;
        
        // Process each job type within its concurrency limit
        foreach (var (jobType, jobOptions) in _options.JobTypes)
        {
            if (!_concurrencyLimits.TryGetValue(jobType, out var limit))
                continue;

            if (_lockGatedJobTypes.Contains(jobType))
            {
                bothChatAndEmbeddingsUseLocalAi ??= await BothChatAndEmbeddingsUseLocalAiAsync(ct);
                if (bothChatAndEmbeddingsUseLocalAi.Value)
                {
                    hasActiveConversationLock ??= await HasActiveConversationLockAsync(ct);
                    if (ConversationLockJobGate.ShouldDeferJobType(
                            jobType,
                            _options.ConversationLockGate,
                            hasActiveConversationLock.Value,
                            bothChatAndEmbeddingsUseLocalAi.Value))
                    {
                        MaybeLogLockGateActive();
                        continue;
                    }
                }
            }

            // Check if we can process more jobs of this type
            while (limit.CurrentCount > 0 && !ct.IsCancellationRequested)
            {
                if (!await limit.WaitAsync(0, ct))
                    break;

                JobQueue? claimedJob;
                using (var claimScope = _serviceProvider.CreateScope())
                {
                    var jobQueueService = claimScope.ServiceProvider.GetRequiredService<IJobQueueService>();
                    claimedJob = await jobQueueService.TryClaimAsync(jobType, jobOptions.LeaseSeconds, ct);
                }

                if (claimedJob == null)
                {
                    limit.Release();
                    break;
                }

                var task = ProcessClaimedJobAsync(claimedJob, jobType, jobOptions, limit, ct);
                tasks.Add(task);

                if (tasks.Count >= 10) break;
            }
        }
        
        // Don't wait for completion - allows API to remain responsive
        if (tasks.Count > 0)
        {
            _ = Task.WhenAll(tasks).ContinueWith(t => 
            {
                if (t.Exception != null)
                    _logger.LogError(t.Exception, "Background job processing errors");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task<bool> HasActiveConversationLockAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await context.ConversationLocks.AnyAsync(l => l.ExpiresAt > now, ct);
    }

    private async Task<bool> BothChatAndEmbeddingsUseLocalAiAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var eligibility = scope.ServiceProvider.GetService<IConversationLockGateEligibility>();
        if (eligibility is null)
        {
            return false;
        }

        return await eligibility.BothUseLocalAiAsync(ct);
    }

    private void MaybeLogLockGateActive()
    {
        var now = DateTime.UtcNow;
        var throttle = TimeSpan.FromSeconds(Math.Max(5, _options.ConversationLockGate.LogThrottleSeconds));
        if (now - _lastLockGateLogUtc < throttle)
        {
            return;
        }

        _lastLockGateLogUtc = now;
        _logger.LogDebug(
            "Deferring extraction/indexing job claims while chat and embeddings use local AI and at least one conversation lock is active");
    }

    private async Task ProcessClaimedJobAsync(JobQueue claimedJob, string jobType, JobTypeOptions jobOptions, SemaphoreSlim limit, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var jobQueueService = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

            _activeExecutionRegistry.MarkActive(claimedJob.Id, claimedJob.ClaimToken);
            _logger.LogDebug("Processing job {JobId} of type {JobType}", claimedJob.Id, claimedJob.JobType);

            // Find the appropriate handler
            if (!_jobHandlers.TryGetValue(claimedJob.JobType, out var handler))
            {
                _logger.LogError("No handler found for job type {JobType}, failing job {JobId}", claimedJob.JobType, claimedJob.Id);
                await jobQueueService.FailAsync(
                    claimedJob.Id,
                    claimedJob.ClaimToken,
                    $"No handler registered for job type: {claimedJob.JobType}",
                    JobFailureClass.PermanentMissingInput,
                    ct);
                return;
            }

            // Process the job
            JobExecutionResult result;
            using var processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var leaseRenewalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var leaseRenewalTask = RenewLeaseLoopAsync(
                jobQueueService,
                claimedJob.Id,
                claimedJob.ClaimToken,
                jobOptions.LeaseSeconds,
                processingCts,
                leaseRenewalCts.Token);

            try
            {
                result = await handler.HandleAsync(claimedJob.PayloadJson, processingCts.Token);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Job handler cancelled during shutdown for job {JobId} of type {JobType}",
                    claimedJob.Id,
                    claimedJob.JobType);
                result = JobExecutionResult.ShutdownCancellation(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job handler threw exception for job {JobId} of type {JobType}", claimedJob.Id, claimedJob.JobType);
                result = JobExecutionResult.RetryableTransient(ex.Message);
            }
            finally
            {
                leaseRenewalCts.Cancel();
                try
                {
                    await leaseRenewalTask;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Normal during shutdown.
                }
            }

            // Update job status
            if (result.IsSuccess)
            {
                var completed = await jobQueueService.CompleteAsync(claimedJob.Id, claimedJob.ClaimToken, ct);
                if (completed)
                {
                    _logger.LogInformation("Successfully processed job {JobId} of type {JobType}", claimedJob.Id, claimedJob.JobType);
                }
                else
                {
                    _logger.LogWarning(
                        "Job handler completed for {JobId} ({JobType}) but completion update failed; skipping success confirmation.",
                        claimedJob.Id,
                        claimedJob.JobType);
                }
            }
            else
            {
                var failureClass = result.FailureClass ?? JobFailureClass.RetryableTransient;
                var errorMessage = result.ErrorMessage ?? "Job handler returned failure";
                var failed = await jobQueueService.FailAsync(claimedJob.Id, claimedJob.ClaimToken, errorMessage, failureClass, ct);
                var attemptNumber = claimedJob.Attempts + 1;
                if (failed)
                {
                    _logger.LogError(
                        "Job handler failed for job {JobId} of type {JobType} on attempt {Attempt}/{MaxAttempts} ({FailureClass}). Payload: {PayloadJson}. Error: {Error}",
                        claimedJob.Id,
                        claimedJob.JobType,
                        attemptNumber,
                        claimedJob.MaxAttempts,
                        failureClass,
                        claimedJob.PayloadJson,
                        errorMessage);
                }
                else
                {
                    _logger.LogError(
                        "Job handler failed for {JobId} ({JobType}) on attempt {Attempt}/{MaxAttempts}, and fail update was not applied (likely lease/token mismatch). Payload: {PayloadJson}",
                        claimedJob.Id,
                        claimedJob.JobType,
                        attemptNumber,
                        claimedJob.MaxAttempts,
                        claimedJob.PayloadJson);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job of type {JobType}", jobType);
        }
        finally
        {
            _activeExecutionRegistry.MarkInactive(claimedJob.Id, claimedJob.ClaimToken);
            limit.Release();
        }
    }

    private async Task CleanupExpiredLeasesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var jobQueueService = scope.ServiceProvider.GetRequiredService<IJobQueueService>();
            
            var requeuedCount = await jobQueueService.RequeueExpiredAsync(ct);
            if (requeuedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired job leases", requeuedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired leases");
        }
    }

    private async Task RenewLeaseLoopAsync(
        IJobQueueService jobQueueService,
        Guid jobId,
        Guid claimToken,
        int leaseSeconds,
        CancellationTokenSource processingCts,
        CancellationToken ct)
    {
        var renewEvery = TimeSpan.FromSeconds(Math.Max(5, leaseSeconds / 2));
        var extendBySeconds = Math.Max(10, leaseSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(renewEvery, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var renewed = await jobQueueService.RenewLeaseAsync(jobId, claimToken, extendBySeconds, ct);
                if (!renewed)
                {
                    _logger.LogWarning(
                        "Failed to renew lease for job {JobId}; canceling active handler work and stopping lease renewal loop.",
                        jobId);
                    try
                    {
                        processingCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Handler scope already disposed.
                    }
                    break;
                }

                _activeExecutionRegistry.Touch(jobId, claimToken);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lease renewal error for job {JobId}", jobId);
            }
        }
    }

    private async Task RequeueProcessingJobsOnStartupAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var jobQueueService = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

            var requeuedCount = await jobQueueService.RequeueAllProcessingAsync(ct);
            if (requeuedCount > 0)
            {
                _logger.LogInformation("Requeued {Count} in-flight jobs on startup", requeuedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requeueing in-flight jobs on startup");
        }
    }

    public override void Dispose()
    {
        foreach (var limit in _concurrencyLimits.Values)
        {
            limit.Dispose();
        }
        base.Dispose();
    }
}
