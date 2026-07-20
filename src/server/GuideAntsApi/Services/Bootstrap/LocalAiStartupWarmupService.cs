using GuideAnts.Logging;
using GuideAntsApi.Configuration;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalAiStartupWarmupService
{
    bool IsWarmupInProgress { get; }

    Task WarmupAllAsync(CancellationToken cancellationToken = default);

    Task EnsureDefaultLlamaLoadedAsync(CancellationToken cancellationToken = default);

    Task EnsureAuxiliaryServicesLoadedAsync(CancellationToken cancellationToken = default);

    Task UnloadAuxiliaryServicesAsync(CancellationToken cancellationToken = default);

    Task<LocalServiceReconcileResult> ReconcileLocalServiceAsync(
        string serviceId,
        string? requestedModelRef = null,
        CancellationToken cancellationToken = default);

    Task<LocalServiceReconcileResult> PowerOffLocalServiceEngineAsync(
        string serviceId,
        CancellationToken cancellationToken = default);
}

public enum LocalServiceReconcileOutcome
{
    Warm,
    Idle,
    NotActiveProvider,
    Unavailable,
    RoutingUnknown,
    Timeout,
    Failed
}

public sealed record LocalServiceReconcileResult(LocalServiceReconcileOutcome Outcome, string? Detail = null);

public interface ILocalAiWarmupService
{
    bool IsApplyInProgress { get; }

    Task SyncDesiredAndApplyAsync(
        WarmupDesiredBuildOptions? options = null,
        bool waitForCompletion = false,
        CancellationToken cancellationToken = default);

    Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalAiStartupWarmupService : ILocalAiStartupWarmupService, ILocalAiWarmupService
{
    private int _warmupInProgress;

    public bool IsWarmupInProgress => Volatile.Read(ref _warmupInProgress) > 0;

    public bool IsApplyInProgress => IsWarmupInProgress;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(45);

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILocalAiDesiredStateBuilder _desiredStateBuilder;
    private readonly ILocalAiWarmupOrchestrationClient _orchestrationClient;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LocalAiStartupWarmupService> _logger;

    public LocalAiStartupWarmupService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILocalAiDesiredStateBuilder desiredStateBuilder,
        ILocalAiWarmupOrchestrationClient orchestrationClient,
        IServiceModeResolver serviceModeResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<LocalAiStartupWarmupService> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _desiredStateBuilder = desiredStateBuilder;
        _orchestrationClient = orchestrationClient;
        _serviceModeResolver = serviceModeResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task WarmupAllAsync(CancellationToken cancellationToken = default) =>
        SyncDesiredAndApplyAsync(waitForCompletion: false, cancellationToken: cancellationToken);

    public Task EnsureDefaultLlamaLoadedAsync(CancellationToken cancellationToken = default) =>
        SyncDesiredAndApplyAsync(waitForCompletion: true, cancellationToken: cancellationToken);

    public Task EnsureAuxiliaryServicesLoadedAsync(CancellationToken cancellationToken = default) =>
        SyncDesiredAndApplyAsync(waitForCompletion: true, cancellationToken: cancellationToken);

    public Task UnloadAuxiliaryServicesAsync(CancellationToken cancellationToken = default) =>
        SyncDesiredAndApplyAsync(
            new WarmupDesiredBuildOptions { ForceAuxiliaryIdle = true },
            waitForCompletion: true,
            cancellationToken: cancellationToken);

    public Task SyncDesiredAndApplyAsync(
        WarmupDesiredBuildOptions? options = null,
        bool waitForCompletion = false,
        CancellationToken cancellationToken = default) =>
        SyncDesiredAndApplyCoreAsync(options, waitForCompletion, allowRetry: true, cancellationToken);

    private async Task SyncDesiredAndApplyCoreAsync(
        WarmupDesiredBuildOptions? options,
        bool waitForCompletion,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        if (!IsOrchestrationConfigured())
        {
            _logger.LogDebug("Skipping warmup sync: local AI orchestration is not configured.");
            return;
        }

        var entered = Interlocked.CompareExchange(ref _warmupInProgress, 1, 0) == 0;
        if (!entered)
        {
            if (waitForCompletion)
            {
                await WaitForApplyCompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            if (allowRetry && options is not null)
            {
                await SyncDesiredAndApplyCoreAsync(options, waitForCompletion, allowRetry: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        try
        {
            await ProjectImageGenerationBundlesIfConfiguredAsync(options, cancellationToken).ConfigureAwait(false);

            await EnsureConfiguredLocalSelectionsSyncedAsync(cancellationToken).ConfigureAwait(false);

            var ini = await _desiredStateBuilder.BuildIniAsync(options, cancellationToken).ConfigureAwait(false);
            var writeResult = await _orchestrationClient
                .PutDesiredAsync(ini, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (await ShouldRequestWarmupApplyAsync(writeResult, cancellationToken).ConfigureAwait(false))
            {
                if (writeResult.Changed)
                {
                    _logger.LogInformation(
                        "Warmup desired INI was out of date; wrote revision {Revision}.",
                        writeResult.Revision);
                }
                else
                {
                    _logger.LogInformation(
                        "Warmup desired INI is correct; applying revision {Revision} to engines.",
                        writeResult.Revision);
                }

                await _orchestrationClient.ApplyAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug(
                    "Warmup desired INI is correct and applied at revision {Revision}.",
                    writeResult.Revision);
            }

            if (waitForCompletion)
            {
                await WaitForApplyCompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local AI warmup sync failed.");
            if (waitForCompletion)
            {
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _warmupInProgress);
        }
    }

    public Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _orchestrationClient.GetStatusAsync(cancellationToken);

    public async Task<LocalServiceReconcileResult> PowerOffLocalServiceEngineAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocalAuxiliaryService(serviceId))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Unavailable,
                $"Service '{serviceId}' does not support local engine power-off.");
        }

        if (!IsOrchestrationConfigured())
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Unavailable,
                "Local warmup orchestration is not configured.");
        }

        var options = new WarmupDesiredBuildOptions
        {
            ServiceDesiredOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [serviceId] = "idle",
            },
        };

        try
        {
            await SyncDesiredAndApplyAsync(options, waitForCompletion: true, cancellationToken)
                .ConfigureAwait(false);
            return await MapServiceOutcomeAsync(serviceId, expectWarm: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Power-off reconcile failed for '{ServiceId}'.", LogValueSanitizer.Sanitize(serviceId));
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Failed, ex.Message);
        }
    }

    public async Task<LocalServiceReconcileResult> ReconcileLocalServiceAsync(
        string serviceId,
        string? requestedModelRef = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocalAuxiliaryService(serviceId))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Unavailable,
                $"Service '{serviceId}' does not support local reconcile.");
        }

        if (!IsOrchestrationConfigured())
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Unavailable,
                "Local warmup orchestration is not configured.");
        }

        var routing = await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (await TryEnsureLocalProviderRoutedForModelOperationAsync(
                serviceId,
                routing,
                requestedModelRef,
                cancellationToken)
            .ConfigureAwait(false))
        {
            routing = await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }

        if (routing == LocalRoutingDesiredState.Unknown)
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.RoutingUnknown,
                $"Routing for '{serviceId}' could not be resolved.");
        }

        if (routing == LocalRoutingDesiredState.Idle && !string.IsNullOrWhiteSpace(requestedModelRef))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.NotActiveProvider,
                $"'{serviceId}' is not the active provider; nothing was loaded.");
        }

        WarmupDesiredBuildOptions options;
        if (routing == LocalRoutingDesiredState.Idle)
        {
            options = new WarmupDesiredBuildOptions
            {
                ServiceDesiredOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [serviceId] = "idle",
                },
            };
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(requestedModelRef))
            {
                var trimmedRef = requestedModelRef.Trim();
                if (!LocalServiceModelRefRules.IsLoadableLocalModelRef(trimmedRef))
                {
                    var refKind = string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
                        ? "bundle id"
                        : "local model path";
                    return new LocalServiceReconcileResult(
                        LocalServiceReconcileOutcome.Failed,
                        $"Model reference '{trimmedRef}' is not a valid {refKind}.");
                }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
                    await settings
                        .SetServiceModeModelIdAsync(serviceId, trimmedRef, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not persist configured model ref for '{ServiceId}'.",
                        LogValueSanitizer.Sanitize(serviceId));
                    return new LocalServiceReconcileResult(
                        LocalServiceReconcileOutcome.Failed,
                        ex.Message);
                }
            }

            else
            {
                var persistedRef = await ResolvePersistedLocalModeModelRefAsync(serviceId, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(persistedRef))
                {
                    var refKind = string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
                        ? "bundle"
                        : "model";
                    return new LocalServiceReconcileResult(
                        LocalServiceReconcileOutcome.Failed,
                        $"No local {refKind} is configured in ServiceModes. "
                        + $"Select an active local {refKind} before loading.");
                }
            }

            options = new WarmupDesiredBuildOptions();
        }

        try
        {
            await SyncDesiredAndApplyAsync(options, waitForCompletion: true, cancellationToken)
                .ConfigureAwait(false);
            return await MapServiceOutcomeAsync(
                    serviceId,
                    expectWarm: routing == LocalRoutingDesiredState.Warm,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile failed for '{ServiceId}'.", LogValueSanitizer.Sanitize(serviceId));
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Failed, ex.Message);
        }
    }

    private async Task EnsureConfiguredLocalSelectionsSyncedAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        await ConfiguredLocalServiceSelectionSync
            .SyncAllWarmLocalServicesAsync(
                settings,
                _configuration,
                _httpClientFactory,
                IsLocalRoutingWarmAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsLocalRoutingWarmAsync(string serviceId, CancellationToken cancellationToken) =>
        await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false)
        == LocalRoutingDesiredState.Warm;

    private async Task<bool> ShouldRequestWarmupApplyAsync(
        WarmupDesiredWriteResult writeResult,
        CancellationToken cancellationToken)
    {
        if (writeResult.Changed)
        {
            return true;
        }

        var status = await _orchestrationClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.DesiredRevision > status.AppliedRevision)
        {
            return true;
        }

        if (string.Equals(status.ApplyStatus, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.ApplyStatus, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return OrchestratorNeedsLoad(status);
    }

    private static bool OrchestratorNeedsLoad(WarmupStatusDocument status)
    {
        foreach (var serviceStatus in status.Services.Values)
        {
            if (string.Equals(serviceStatus.Desired, "on", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(serviceStatus.Phase, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<LocalServiceReconcileResult> MapServiceOutcomeAsync(
        string serviceId,
        bool expectWarm,
        CancellationToken cancellationToken)
    {
        var status = await _orchestrationClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Services.TryGetValue(serviceId, out var serviceStatus))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Failed,
                $"Warmup status did not include '{serviceId}'.");
        }

        if (string.Equals(serviceStatus.Phase, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Failed,
                serviceStatus.Error ?? "Orchestrator reported failure.");
        }

        if (!string.IsNullOrWhiteSpace(serviceStatus.Error)
            && !string.Equals(serviceStatus.Phase, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Failed,
                serviceStatus.Error);
        }

        if (expectWarm)
        {
            if (string.Equals(serviceStatus.Phase, "ready", StringComparison.OrdinalIgnoreCase)
                && HasLoadedRef(serviceStatus))
            {
                return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Warm);
            }

            if (string.Equals(status.ApplyStatus, "applying", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.ApplyStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                return new LocalServiceReconcileResult(
                    LocalServiceReconcileOutcome.Timeout,
                    $"Timed out waiting for '{serviceId}' to become warm.");
            }

            return new LocalServiceReconcileResult(
                LocalServiceReconcileOutcome.Timeout,
                $"Service '{serviceId}' did not reach warm state.");
        }

        if (string.Equals(serviceStatus.Phase, "idle", StringComparison.OrdinalIgnoreCase)
            && !HasLoadedRef(serviceStatus))
        {
            return new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Idle);
        }

        return new LocalServiceReconcileResult(
            LocalServiceReconcileOutcome.Timeout,
            $"Service '{serviceId}' did not reach idle state.");
    }

    private static bool HasLoadedRef(WarmupServiceStatus serviceStatus) =>
        !string.IsNullOrWhiteSpace(serviceStatus.RouterAlias)
        || !string.IsNullOrWhiteSpace(serviceStatus.ModelId)
        || !string.IsNullOrWhiteSpace(serviceStatus.BundleId);

    private async Task WaitForApplyCompleteAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ApplyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _orchestrationClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.DesiredRevision <= status.AppliedRevision
                && !string.Equals(status.ApplyStatus, "applying", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status.ApplyStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(status.ApplyStatus, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(status.ApplyError ?? "Warmup apply failed.");
                }

                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for warmup orchestrator to finish applying.");
    }

    private bool IsOrchestrationConfigured() =>
        RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]);

    private static bool IsLocalAuxiliaryService(string serviceId) =>
        string.Equals(serviceId, RoutedServiceNames.SpeechTranscription, StringComparison.Ordinal)
        || string.Equals(serviceId, RoutedServiceNames.Embeddings, StringComparison.Ordinal)
        || string.Equals(serviceId, RoutedServiceNames.SpeechSynthesis, StringComparison.Ordinal)
        || string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal);

    private async Task<string?> ResolvePersistedLocalModeModelRefAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var localProviderSection = serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };

        if (localProviderSection is null)
        {
            return null;
        }

        var modes = await _serviceModeResolver
            .GetModesAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);
        var localMode = modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, localProviderSection, StringComparison.OrdinalIgnoreCase));
        return localMode?.ModelId?.Trim() is { Length: > 0 } modelId
            ? modelId
            : null;
    }

    private enum LocalRoutingDesiredState
    {
        Warm,
        Idle,
        Unknown,
    }

    private static string? TryGetLocalProviderId(string serviceId) =>
        serviceId switch
        {
            RoutedServiceNames.ImageGeneration => ServiceProviderIds.ImageGenerationLocalSdHttp,
            RoutedServiceNames.Embeddings => ServiceProviderIds.EmbeddingsLocalEmbHttp,
            RoutedServiceNames.SpeechTranscription => ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
            RoutedServiceNames.SpeechSynthesis => ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
            _ => null,
        };

    private async Task<bool> TryEnsureLocalProviderRoutedForModelOperationAsync(
        string serviceId,
        LocalRoutingDesiredState routing,
        string? requestedModelRef,
        CancellationToken cancellationToken)
    {
        var localProviderId = TryGetLocalProviderId(serviceId);
        if (localProviderId is null)
        {
            return false;
        }

        var shouldEnsure = routing == LocalRoutingDesiredState.Unknown
            || (routing == LocalRoutingDesiredState.Idle && !string.IsNullOrWhiteSpace(requestedModelRef));
        if (!shouldEnsure)
        {
            return false;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
            await settings.EnsureServiceModeExistsAsync(serviceId, localProviderId, cancellationToken)
                .ConfigureAwait(false);
            await settings.SetServiceActiveProviderAsync(serviceId, localProviderId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Auto-activated local provider for '{ServiceId}' before local model reconcile.",
                LogValueSanitizer.Sanitize(serviceId));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not auto-activate local provider for '{ServiceId}' before local model reconcile.",
                LogValueSanitizer.Sanitize(serviceId));
            return false;
        }
    }

    private async Task<LocalRoutingDesiredState> ResolveLocalRoutingDesiredStateAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var expectedLocalProviderSection = serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };

        if (expectedLocalProviderSection is null)
        {
            return LocalRoutingDesiredState.Warm;
        }

        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(serviceId, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(mode.ProviderSection, expectedLocalProviderSection, StringComparison.Ordinal))
            {
                return LocalRoutingDesiredState.Warm;
            }

            return LocalRoutingDesiredState.Idle;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve routing mode for {ServiceId}; treating as unknown.",
                LogValueSanitizer.Sanitize(serviceId));
            return LocalRoutingDesiredState.Unknown;
        }
    }

    private async Task ProjectImageGenerationBundlesIfConfiguredAsync(
        WarmupDesiredBuildOptions? options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(LocalServiceAdminRouting.ResolveAdminBase("ImageGeneration", _configuration)))
        {
            return;
        }

        if (!await IsImageGenerationWarmDesiredAsync(options, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Skipping ImageGeneration bundle projection: service is not warm-desired for this warmup sync.");
            return;
        }

        using var projectionScope = _scopeFactory.CreateScope();
        var bootstrapper = projectionScope.ServiceProvider.GetService<IImageGenerationBundleDefinitionBootstrapper>();
        if (bootstrapper is null)
        {
            _logger.LogWarning("ImageGeneration bundle bootstrapper is not registered; skipping projection.");
            return;
        }

        await bootstrapper.ProjectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsImageGenerationWarmDesiredAsync(
        WarmupDesiredBuildOptions? options,
        CancellationToken cancellationToken)
    {
        if (options?.ForceAuxiliaryIdle == true)
        {
            return false;
        }

        if (options?.ServiceDesiredOverrides is not null
            && options.ServiceDesiredOverrides.TryGetValue(
                RoutedServiceNames.ImageGeneration,
                out var desiredOverride))
        {
            var normalized = desiredOverride.Trim().ToLowerInvariant();
            return normalized is "warm" or "on";
        }

        return await IsLocalRoutingWarmAsync(RoutedServiceNames.ImageGeneration, cancellationToken)
            .ConfigureAwait(false);
    }
}
