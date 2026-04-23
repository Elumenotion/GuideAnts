namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Persists per-alias llama-server router preset knobs (context, prompt cache) into
/// <c>router-models.ini</c> via the guideants-ai llama-admin service and signals
/// llama-server to reload so argv changes take effect.
/// </summary>
public interface ILlamaRouterIniSyncService
{
    /// <summary>
    /// Reconciles <c>router-models.ini</c> for <see cref="LocalRuntimeConfiguration.RouterModelId"/>
    /// when a llama-admin entry exists. Non-null values set <c>ctx-size</c> / <c>cache-ram</c>;
    /// null values remove those keys so the container defaults (e.g. <c>GA_LLAMA_CTX_SIZE</c>) apply.
    /// </summary>
    Task TrySyncFromLocalRuntimeAsync(LocalRuntimeConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class LlamaRouterIniSyncService : ILlamaRouterIniSyncService
{
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly ILogger<LlamaRouterIniSyncService> _logger;

    public LlamaRouterIniSyncService(
        ILlamaRuntimeAdminClient adminClient,
        ILogger<LlamaRouterIniSyncService> logger)
    {
        _adminClient = adminClient;
        _logger = logger;
    }

    public async Task TrySyncFromLocalRuntimeAsync(
        LocalRuntimeConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LlamaAdminRouterEntryDto> entries;
        try
        {
            entries = await _adminClient.GetRouterEntriesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping router INI sync for '{Alias}': could not list router entries from llama-admin.",
                configuration.RouterModelId);
            return;
        }

        var match = entries.FirstOrDefault(e =>
            string.Equals(e.Alias, configuration.RouterModelId, StringComparison.Ordinal));

        if (match is null)
        {
            _logger.LogInformation(
                "Router INI sync skipped: no llama-admin entry for alias '{Alias}' (paths not registered yet).",
                configuration.RouterModelId);
            return;
        }

        try
        {
            await _adminClient
                .AddOrUpdateRouterEntryAsync(
                    configuration.RouterModelId,
                    match.ModelPath ?? string.Empty,
                    match.MmprojPath ?? string.Empty,
                    configuration.RouterContextSize,
                    configuration.RouterCacheRamMib,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to sync router INI for alias '{Alias}'.",
                configuration.RouterModelId);
            throw;
        }
    }
}
