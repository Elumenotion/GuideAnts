namespace GuideAntsApi.Services.Components;

/// <summary>
/// Runs host-folder mount reconciliation once on API startup. This is the authoritative
/// source of truth for symlink state; the post-restart helper callback is best-effort only.
/// </summary>
public sealed class HostFolderMountStartupReconciliationService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HostFolderMountStartupReconciliationService> _logger;

    public HostFolderMountStartupReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<HostFolderMountStartupReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting host folder mount reconciliation");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mountService = scope.ServiceProvider.GetRequiredService<IHostFolderMountService>();
            await mountService.ReconcileAllMountsAsync(cancellationToken);
            _logger.LogInformation("Host folder mount startup reconciliation completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Host folder mount startup reconciliation failed; symlink state will be retried on the next reconcile trigger");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
