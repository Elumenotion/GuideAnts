using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    public BackgroundJobProcessor(
        IServiceProvider serviceProvider,
        IOptions<JobProcessorOptions> options,
        IActiveJobExecutionRegistry activeExecutionRegistry,
        ILogger<BackgroundJobProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _activeExecutionRegistry = activeExecutionRegistry;
        _logger = logger;
        
        // Initialize concurrency limits for each job type
        _concurrencyLimits = new Dictionary<string, SemaphoreSlim>();
        foreach (var (jobType, jobOptions) in _options.JobTypes)
        {
            _concurrencyLimits[jobType] = new SemaphoreSlim(jobOptions.MaxConcurrency, jobOptions.MaxConcurrency);
        }

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
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); // Back off on error
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

        _logger.LogInformation("Initialized {HandlerCount} job handlers", _jobHandlers.Count);
        return Task.CompletedTask;
    }

    private async Task ProcessAvailableJobsAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();
        
        // Process each job type within its concurrency limit
        foreach (var (jobType, jobOptions) in _options.JobTypes)
        {
            if (!_concurrencyLimits.TryGetValue(jobType, out var limit))
                continue;

            // Check if we can process more jobs of this type
            while (limit.CurrentCount > 0 && !ct.IsCancellationRequested)
            {
                if (!await limit.WaitAsync(0, ct)) // Non-blocking check
                    break;

                // Launch job processing on background thread
                var task = ProcessSingleJobAsync(jobType, jobOptions, limit, ct);
                tasks.Add(task);

                // Don't create too many tasks at once
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

    private async Task ProcessSingleJobAsync(string jobType, JobTypeOptions jobOptions, SemaphoreSlim limit, CancellationToken ct)
    {
        JobQueue? claimedJob = null;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var jobQueueService = scope.ServiceProvider.GetRequiredService<IJobQueueService>();

            // Try to claim a job
            claimedJob = await jobQueueService.TryClaimAsync(jobType, jobOptions.LeaseSeconds, ct);
            if (claimedJob == null)
            {
                return; // No jobs available
            }

            _activeExecutionRegistry.MarkActive(claimedJob.Id, claimedJob.ClaimToken);
            _logger.LogDebug("Processing job {JobId} of type {JobType}", claimedJob.Id, claimedJob.JobType);

            // Find the appropriate handler
            if (!_jobHandlers.TryGetValue(claimedJob.JobType, out var handler))
            {
                _logger.LogError("No handler found for job type {JobType}, failing job {JobId}", claimedJob.JobType, claimedJob.Id);
                await jobQueueService.FailAsync(claimedJob.Id, claimedJob.ClaimToken, $"No handler registered for job type: {claimedJob.JobType}", ct: ct);
                return;
            }

            // Process the job
            bool success;
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
                success = await handler.HandleAsync(claimedJob.PayloadJson, processingCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job handler threw exception for job {JobId} of type {JobType}", claimedJob.Id, claimedJob.JobType);
                success = false;
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
            if (success)
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
                var failed = await jobQueueService.FailAsync(claimedJob.Id, claimedJob.ClaimToken, "Job handler returned false", ct: ct);
                if (failed)
                {
                    _logger.LogWarning("Job handler failed for job {JobId} of type {JobType}", claimedJob.Id, claimedJob.JobType);
                }
                else
                {
                    _logger.LogWarning(
                        "Job handler failed for {JobId} ({JobType}) but fail update was not applied (likely lease/token mismatch).",
                        claimedJob.Id,
                        claimedJob.JobType);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job of type {JobType}", jobType);
        }
        finally
        {
            if (claimedJob != null)
            {
                _activeExecutionRegistry.MarkInactive(claimedJob.Id, claimedJob.ClaimToken);
            }

            limit.Release(); // Always release the semaphore
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
