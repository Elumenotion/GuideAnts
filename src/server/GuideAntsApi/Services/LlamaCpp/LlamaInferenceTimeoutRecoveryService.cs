using System.Collections.Concurrent;
using AntRunner.Chat.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed class LlamaInferenceTimeoutRecoveryOptions
{
    public const string SectionName = "LlamaInferenceTimeoutRecovery";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Bounds router model unload plus confirmation. llama.cpp force-kills the model child after
    /// its per-preset stop-timeout (10 seconds by default), so this must be longer than that.
    /// </summary>
    public int UnloadTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Bounds model load and readiness confirmation after either recovery path.
    /// </summary>
    public int LoadTimeoutSeconds { get; set; } = 900;

    public int PollIntervalMilliseconds { get; set; } = 500;
}

/// <summary>
/// Owns the destructive response to a proven llama.cpp inference timeout.
///
/// The first timeout opens the alias circuit immediately. Recovery is single-flight per alias
/// and globally serialized because the router is configured for one resident model. It first
/// unloads the model child (which force-kills it after llama.cpp's stop-timeout), confirms the
/// child is gone, and reloads the alias. If that does not converge, it invokes the admin service's
/// process-wide SIGTERM/SIGKILL restart and then reloads the alias.
/// </summary>
public sealed class LlamaInferenceTimeoutRecoveryService : ILlamaInferenceTimeoutObserver
{
    private readonly ILlamaServerRuntimeClient _runtimeClient;
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly ILlamaRuntimeCoordinator _runtimeCoordinator;
    private readonly LlamaInferenceTimeoutRecoveryOptions _options;
    private readonly ILogger<LlamaInferenceTimeoutRecoveryService> _logger;
    private readonly ConcurrentDictionary<string, RecoveryEntry> _recoveries =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _globalRecoveryGate = new(1, 1);

    public LlamaInferenceTimeoutRecoveryService(
        ILlamaServerRuntimeClient runtimeClient,
        ILlamaRuntimeAdminClient adminClient,
        ILlamaRuntimeCoordinator runtimeCoordinator,
        IOptions<LlamaInferenceTimeoutRecoveryOptions> options,
        ILogger<LlamaInferenceTimeoutRecoveryService> logger)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));
        _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureInferenceAvailableAsync(
        string? routerModelId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled
            || string.IsNullOrWhiteSpace(routerModelId)
            || !_recoveries.TryGetValue(routerModelId, out var recovery))
        {
            return;
        }

        if (!recovery.Failed)
        {
            throw new LlamaRuntimeCrashedException(
                LlamaRuntimeCrashReason.Recovering,
                $"The local model '{routerModelId}' is being forcefully recovered after an inference timeout. Retry after recovery completes.",
                statusCode: null,
                upstreamDetail: null);
        }

        // A process/container may have been repaired manually after automatic recovery failed.
        // Verify that explicit state before reopening the circuit.
        try
        {
            var models = await _runtimeClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (IsAliasLoaded(models, routerModelId))
            {
                _recoveries.TryRemove(new KeyValuePair<string, RecoveryEntry>(routerModelId, recovery));
                _logger.LogInformation(
                    "Cleared failed llama timeout recovery circuit after external repair. RouterModelId={RouterModelId}",
                    LogValueSanitizer.Sanitize(routerModelId));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not verify external repair for failed llama timeout recovery. RouterModelId={RouterModelId}",
                LogValueSanitizer.Sanitize(routerModelId));
        }

        throw new LlamaRuntimeCrashedException(
            LlamaRuntimeCrashReason.Crashed,
            $"Automatic recovery failed for local model '{routerModelId}'. Restart the local model runtime before retrying.",
            statusCode: null,
            upstreamDetail: recovery.Error);
    }

    public Task<LlamaInferenceRecoveryResult> RequestRecoveryAsync(
        string routerModelId,
        int timeoutSeconds)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(new LlamaInferenceRecoveryResult(
                routerModelId,
                Succeeded: false,
                EscalatedToServerRestart: false,
                Error: "Automatic llama inference timeout recovery is disabled."));
        }

        if (string.IsNullOrWhiteSpace(routerModelId) || routerModelId == "(unknown)")
        {
            return Task.FromResult(new LlamaInferenceRecoveryResult(
                routerModelId,
                Succeeded: false,
                EscalatedToServerRestart: false,
                Error: "The timed-out request did not identify a router model alias."));
        }

        var candidate = new RecoveryEntry();
        var recovery = _recoveries.GetOrAdd(routerModelId, candidate);
        if (!ReferenceEquals(recovery, candidate))
        {
            _logger.LogInformation(
                "Joined existing llama timeout recovery. RouterModelId={RouterModelId}",
                LogValueSanitizer.Sanitize(routerModelId));
            return recovery.Completion.Task;
        }

        _logger.LogError(
            "Starting forceful llama timeout recovery. RouterModelId={RouterModelId} InferenceTimeoutSeconds={InferenceTimeoutSeconds}",
            LogValueSanitizer.Sanitize(routerModelId),
            timeoutSeconds);

        _ = CompleteRecoveryAsync(routerModelId, recovery);
        return recovery.Completion.Task;
    }

    private async Task CompleteRecoveryAsync(string routerModelId, RecoveryEntry recovery)
    {
        LlamaInferenceRecoveryResult result;
        try
        {
            result = await RecoverAsync(routerModelId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Forceful llama timeout recovery terminated unexpectedly. RouterModelId={RouterModelId}",
                LogValueSanitizer.Sanitize(routerModelId));
            result = new LlamaInferenceRecoveryResult(
                routerModelId,
                Succeeded: false,
                EscalatedToServerRestart: false,
                Error: ex.Message);
        }

        if (result.Succeeded)
        {
            _recoveries.TryRemove(new KeyValuePair<string, RecoveryEntry>(routerModelId, recovery));
        }
        else
        {
            recovery.MarkFailed(result.Error);
        }

        recovery.Completion.TrySetResult(result);
    }

    private async Task<LlamaInferenceRecoveryResult> RecoverAsync(string routerModelId)
    {
        await _globalRecoveryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await using var aliasLock = await _runtimeCoordinator
                .AcquireAliasLockAsync(routerModelId, CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                await ReplaceModelChildAsync(routerModelId).ConfigureAwait(false);
                _logger.LogInformation(
                    "Forceful llama model-child recovery completed. RouterModelId={RouterModelId}",
                    LogValueSanitizer.Sanitize(routerModelId));
                return new LlamaInferenceRecoveryResult(
                    routerModelId,
                    Succeeded: true,
                    EscalatedToServerRestart: false,
                    Error: null);
            }
            catch (Exception modelRecoveryException)
            {
                _logger.LogError(
                    modelRecoveryException,
                    "Model-child recovery did not converge; escalating to full llama-server restart. RouterModelId={RouterModelId}",
                    LogValueSanitizer.Sanitize(routerModelId));

                try
                {
                    await _adminClient.RestartLlamaServerAsync(CancellationToken.None).ConfigureAwait(false);
                    await LoadAndConfirmAsync(routerModelId).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Full llama-server restart recovery completed. RouterModelId={RouterModelId}",
                        LogValueSanitizer.Sanitize(routerModelId));
                    return new LlamaInferenceRecoveryResult(
                        routerModelId,
                        Succeeded: true,
                        EscalatedToServerRestart: true,
                        Error: null);
                }
                catch (Exception serverRestartException)
                {
                    var error =
                        $"Model-child recovery failed: {modelRecoveryException.Message} "
                        + $"Full llama-server restart failed: {serverRestartException.Message}";
                    _logger.LogCritical(
                        serverRestartException,
                        "Forceful llama timeout recovery failed after full server restart escalation. "
                        + "RouterModelId={RouterModelId} ModelRecoveryError={ModelRecoveryError}",
                        LogValueSanitizer.Sanitize(routerModelId),
                        LogValueSanitizer.Sanitize(modelRecoveryException.Message));
                    return new LlamaInferenceRecoveryResult(
                        routerModelId,
                        Succeeded: false,
                        EscalatedToServerRestart: true,
                        Error: error);
                }
            }
        }
        finally
        {
            _globalRecoveryGate.Release();
        }
    }

    private async Task ReplaceModelChildAsync(string routerModelId)
    {
        using var unloadCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _options.UnloadTimeoutSeconds)));
        await _runtimeClient
            .UnloadModelAsync(routerModelId, unloadCancellation.Token)
            .ConfigureAwait(false);
        await WaitForAliasStateAsync(
            routerModelId,
            desiredState: "unloaded",
            failOnReportedFailure: false,
            unloadCancellation.Token).ConfigureAwait(false);

        await LoadAndConfirmAsync(routerModelId).ConfigureAwait(false);
    }

    private async Task LoadAndConfirmAsync(string routerModelId)
    {
        using var loadCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _options.LoadTimeoutSeconds)));
        await _runtimeClient
            .LoadModelAsync(routerModelId, loadCancellation.Token)
            .ConfigureAwait(false);
        await WaitForAliasStateAsync(
            routerModelId,
            desiredState: "loaded",
            failOnReportedFailure: true,
            loadCancellation.Token).ConfigureAwait(false);
    }

    private async Task WaitForAliasStateAsync(
        string routerModelId,
        string desiredState,
        bool failOnReportedFailure,
        CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(50, _options.PollIntervalMilliseconds));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var models = await _runtimeClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = models.Data.FirstOrDefault(item =>
                string.Equals(item.Id, routerModelId, StringComparison.Ordinal));

            if (model is null)
            {
                throw new InvalidOperationException(
                    $"Router model alias '{routerModelId}' disappeared while waiting for state '{desiredState}'.");
            }

            var state = GetModelState(model);
            if (string.Equals(state, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (failOnReportedFailure && model.Failed)
            {
                throw new InvalidOperationException(
                    $"Router model alias '{routerModelId}' failed while loading (exit code {model.ExitCode?.ToString() ?? "unknown"}).");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsAliasLoaded(LlamaModelsResponse models, string routerModelId) =>
        models.Data.Any(model =>
            string.Equals(model.Id, routerModelId, StringComparison.Ordinal)
            && string.Equals(GetModelState(model), "loaded", StringComparison.OrdinalIgnoreCase)
            && !model.Failed);

    private static string GetModelState(LlamaModelData model) =>
        !string.IsNullOrWhiteSpace(model.Status?.Value)
            ? model.Status.Value
            : model.State;

    private sealed class RecoveryEntry
    {
        private int _failed;

        public TaskCompletionSource<LlamaInferenceRecoveryResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Failed => Volatile.Read(ref _failed) != 0;

        public string? Error { get; private set; }

        public void MarkFailed(string? error)
        {
            Error = error;
            Volatile.Write(ref _failed, 1);
        }
    }
}
