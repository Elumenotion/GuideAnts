namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Background service that periodically cleans up expired conversation locks.
/// Runs every 60 seconds to ensure locks don't accumulate from crashes or disconnects.
/// </summary>
public class LockCleanupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LockCleanupBackgroundService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(60);
    
    public LockCleanupBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<LockCleanupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Lock cleanup background service starting");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
                
                using var scope = _scopeFactory.CreateScope();
                var lockService = scope.ServiceProvider.GetRequiredService<IDistributedConversationLock>();
                
                await lockService.CleanupExpiredLocksAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in lock cleanup background service");
                // Continue running despite errors
            }
        }
        
        _logger.LogInformation("Lock cleanup background service stopping");
    }
}

