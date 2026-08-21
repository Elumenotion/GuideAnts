using GuideAntsApi.Configuration;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Re-submits API-owned lifecycle policy after the executor restarts, and repairs
/// a missing configured local llama model while the API process remains alive.
/// </summary>
public sealed class LocalAiRuntimeWatchdogHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILocalAiStartupWarmupService _warmupService;
    private readonly ILocalAiWarmupOrchestrationClient _orchestrationClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalAiRuntimeWatchdogHostedService> _logger;

    private readonly ILocalAiStackHostResolver _stackHostResolver;

    public LocalAiRuntimeWatchdogHostedService(
        IServiceScopeFactory scopeFactory,
        ILocalAiStartupWarmupService warmupService,
        ILocalAiWarmupOrchestrationClient orchestrationClient,
        ILocalAiStackHostResolver stackHostResolver,
        IConfiguration configuration,
        ILogger<LocalAiRuntimeWatchdogHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _warmupService = warmupService;
        _orchestrationClient = orchestrationClient;
        _stackHostResolver = stackHostResolver;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsLifecycleExecutorConfigured())
        {
            _logger.LogDebug("Local AI lifecycle watchdog disabled: LlamaCpp:BaseUrl is not configured.");
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
                    && (await ExecutorHasNoApiPlanAsync(stoppingToken).ConfigureAwait(false)
                        || (!await IsConfiguredDefaultLlamaLoadedAsync(stoppingToken).ConfigureAwait(false)
                            && !await IsConfiguredDefaultLlamaFailedAsync(stoppingToken).ConfigureAwait(false))))
                {
                    _logger.LogInformation(
                        "Local AI executor needs current API lifecycle policy; submitting a complete plan.");
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

    private bool IsLifecycleExecutorConfigured() => _stackHostResolver.HasAnyConfiguredStack();

    private async Task<bool> ExecutorHasNoApiPlanAsync(CancellationToken cancellationToken)
    {
        var status = await _orchestrationClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return ExecutorNeedsApiPlan(status);
    }

    internal static bool ExecutorNeedsApiPlan(WarmupStatusDocument status) =>
        status.DesiredRevision == 0
            || string.Equals(status.ApplyStatus, "idle", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsConfiguredDefaultLlamaLoadedAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var routerAlias = await ResolveConfiguredDefaultRouterAliasAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        if (routerAlias is null)
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
        using var scope = _scopeFactory.CreateScope();
        var routerAlias = await ResolveConfiguredDefaultRouterAliasAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        if (routerAlias is null)
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

    private static async Task<string?> ResolveConfiguredDefaultRouterAliasAsync(
        IServiceScope scope,
        CancellationToken cancellationToken)
    {
        var settingsService = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        var chatDefaultsSection = await settingsService
            .GetSectionAsync("ChatDefaults", cancellationToken)
            .ConfigureAwait(false);
        var defaultModelId = ChatDefaultsSnapshot.FromSection(chatDefaultsSection).DefaultModelId?.Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return null;
        }

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
            return null;
        }

        try
        {
            return LocalRuntimeConfigurationParser.ParseRequired(defaultModelId, row.RuntimeConfigJson).RouterModelId;
        }
        catch
        {
            return null;
        }
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
