using GuideAntsApi.Configuration;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Re-runs the full local AI warmup when the llama router is reachable but the
/// configured default model is not loaded — for example after a guideants-ai
/// container restart while the web API process stayed up.
/// </summary>
public sealed class LocalAiRuntimeWatchdogHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILocalAiStartupWarmupService _warmupService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalAiRuntimeWatchdogHostedService> _logger;

    public LocalAiRuntimeWatchdogHostedService(
        IServiceScopeFactory scopeFactory,
        ILocalAiStartupWarmupService warmupService,
        IConfiguration configuration,
        ILogger<LocalAiRuntimeWatchdogHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _warmupService = warmupService;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsLocalLlamaWarmupConfigured())
        {
            _logger.LogDebug("Local AI runtime watchdog disabled: LlamaCpp:BaseUrl is not configured.");
            return;
        }

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
                if (!_warmupService.IsWarmupInProgress
                    && !await IsConfiguredDefaultLlamaLoadedAsync(stoppingToken).ConfigureAwait(false)
                    && !await IsConfiguredDefaultLlamaFailedAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Configured default llama model is not loaded; re-running full local AI warmup.");
                    await _warmupService.WarmupAllAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local AI runtime watchdog warmup attempt failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private bool IsLocalLlamaWarmupConfigured()
    {
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            return false;
        }

        var defaultModelId = (_configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(defaultModelId);
    }

    private async Task<bool> IsConfiguredDefaultLlamaLoadedAsync(CancellationToken cancellationToken)
    {
        var defaultModelId = (_configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return true;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuideAntsApi.DataModel.ApplicationDbContext>();
        var row = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == defaultModelId)
            .Select(m => new { m.Provider, m.RuntimeConfigJson, m.IsActive })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null
            || !row.IsActive
            || !string.Equals(row.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
        {
            return true;
        }

        string routerAlias;
        try
        {
            routerAlias = LocalRuntimeConfigurationParser.ParseRequired(defaultModelId, row.RuntimeConfigJson).RouterModelId;
        }
        catch
        {
            return true;
        }

        var llamaClient = scope.ServiceProvider.GetRequiredService<ILlamaServerRuntimeClient>();
        LlamaModelsResponse models;
        try
        {
            models = await llamaClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local AI runtime watchdog could not query llama router inventory.");
            return false;
        }

        return models.Data.Any(m =>
            string.Equals(m.Id, routerAlias, StringComparison.Ordinal)
            && IsRouterModelLoaded(m));
    }

    private async Task<bool> IsConfiguredDefaultLlamaFailedAsync(CancellationToken cancellationToken)
    {
        var defaultModelId = (_configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuideAntsApi.DataModel.ApplicationDbContext>();
        var row = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == defaultModelId)
            .Select(m => new { m.Provider, m.RuntimeConfigJson, m.IsActive })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null
            || !row.IsActive
            || !string.Equals(row.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
        {
            return false;
        }

        string routerAlias;
        try
        {
            routerAlias = LocalRuntimeConfigurationParser.ParseRequired(defaultModelId, row.RuntimeConfigJson).RouterModelId;
        }
        catch
        {
            return false;
        }

        var llamaClient = scope.ServiceProvider.GetRequiredService<ILlamaServerRuntimeClient>();
        LlamaModelsResponse models;
        try
        {
            models = await llamaClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        return models.Data.Any(m =>
            string.Equals(m.Id, routerAlias, StringComparison.Ordinal)
            && IsRouterModelFailed(m));
    }

    private static bool IsRouterModelLoaded(LlamaModelData model)
    {
        if (!string.IsNullOrWhiteSpace(model.Status?.Value))
        {
            return string.Equals(model.Status.Value, "loaded", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(model.State))
        {
            return string.Equals(model.State, "loaded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsRouterModelFailed(LlamaModelData model)
    {
        if (model.Failed)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(model.Status?.Value))
        {
            var status = model.Status.Value;
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(model.State))
        {
            return string.Equals(model.State, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.State, "error", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
