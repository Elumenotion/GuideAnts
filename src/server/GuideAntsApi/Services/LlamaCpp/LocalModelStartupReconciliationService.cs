namespace GuideAntsApi.Services.LlamaCpp;

using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

/// <summary>
/// Reconciles in-flight local model operations on startup and periodically while
/// the API is alive. Client polling and one-shot background tasks are not sufficient
/// for long downloads: without a server-side sweep, a lost journal or abandoned UI
/// poller leaves a non-terminal row that permanently blocks the alias.
/// </summary>
public sealed class LocalModelStartupReconciliationService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocalModelStartupReconciliationService> _logger;

    public LocalModelStartupReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<LocalModelStartupReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local model in-flight operation reconciliation failed.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var operationService = scope.ServiceProvider.GetRequiredService<ILocalModelOperationService>();
        await operationService.ReconcileInFlightOperationsAsync(cancellationToken).ConfigureAwait(false);

        var lifecycleOperationService = scope.ServiceProvider
            .GetRequiredService<ILocalModelLifecycleOperationService>();
        await lifecycleOperationService
            .ReconcileInFlightLifecycleOperationsAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
