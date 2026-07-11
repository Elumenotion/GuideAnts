namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Persists per-alias llama-server router preset knobs into router-models.ini via llama-admin.
/// Context/cache are set by onboarding or migration, not from runtime JSON.
/// </summary>
public interface ILlamaRouterIniSyncService
{
    Task SyncAliasContextAndCacheAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        int? contextSize,
        int? cacheRamMib,
        CancellationToken cancellationToken = default);
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

    public async Task SyncAliasContextAndCacheAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        int? contextSize,
        int? cacheRamMib,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _adminClient
                .AddOrUpdateRouterEntryAsync(alias, modelPath, mmprojPath, contextSize, cacheRamMib, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync router INI for alias '{Alias}'.", alias);
            throw;
        }
    }
}
