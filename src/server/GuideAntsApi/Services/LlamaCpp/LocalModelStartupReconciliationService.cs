namespace GuideAntsApi.Services.LlamaCpp;

using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

/// <summary>
/// Reconciles in-flight local model operations on startup.
/// </summary>
public sealed class LocalModelStartupReconciliationService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocalModelStartupReconciliationService> _logger;

    public LocalModelStartupReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<LocalModelStartupReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var operationService = scope.ServiceProvider.GetRequiredService<ILocalModelOperationService>();
            await operationService.ReconcileInFlightOperationsAsync(cancellationToken).ConfigureAwait(false);

            var lifecycleOperationService = scope.ServiceProvider.GetRequiredService<ILocalModelLifecycleOperationService>();
            await lifecycleOperationService.ReconcileInFlightLifecycleOperationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local model startup reconciliation failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
