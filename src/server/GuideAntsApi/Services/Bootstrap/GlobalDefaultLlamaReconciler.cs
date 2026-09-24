using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Reconciles llama instances with the global default chat model. This is the direct
/// actuation of the unload rule: when the global default model changes, the API
/// unloads every loaded alias that is not the new default, on every in-use instance,
/// and loads the new default on its own instance if it is not already loaded there.
/// The API performs each load and unload itself against the instance's llama runtime
/// (models/load, models/unload); nothing is delegated to the instances.
/// </summary>
public interface IGlobalDefaultLlamaReconciler
{
    Task ReconcileWithGlobalDefaultAsync(CancellationToken cancellationToken = default);
}

public sealed class GlobalDefaultLlamaReconciler : IGlobalDefaultLlamaReconciler
{
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VerifyPollInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILocalAiStackHostResolver _stackHostResolver;
    private readonly ILlamaStackRuntimeClientProvider _clientProvider;
    private readonly INotebookChatAliasState _notebookChatAliasState;
    private readonly ILlamaRuntimeCoordinator _coordinator;
    private readonly ILogger<GlobalDefaultLlamaReconciler> _logger;
    private readonly Dictionary<string, string> _stackApiKeysByBase = new(StringComparer.OrdinalIgnoreCase);

    public GlobalDefaultLlamaReconciler(
        IServiceScopeFactory scopeFactory,
        ILocalAiStackHostResolver stackHostResolver,
        ILlamaStackRuntimeClientProvider clientProvider,
        INotebookChatAliasState notebookChatAliasState,
        ILlamaRuntimeCoordinator coordinator,
        ILogger<GlobalDefaultLlamaReconciler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _stackHostResolver = stackHostResolver ?? throw new ArgumentNullException(nameof(stackHostResolver));
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _notebookChatAliasState = notebookChatAliasState ?? throw new ArgumentNullException(nameof(notebookChatAliasState));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ReconcileWithGlobalDefaultAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
        var instances = await _stackHostResolver.GetAllConfiguredInstancesAsync(cancellationToken).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return;
        }

        var defaultRow = db is null
            ? null
            : await ResolveDefaultLlamaRowAsync(db, cancellationToken).ConfigureAwait(false);

        await ResolveStackApiKeysAsync(db, cancellationToken).ConfigureAwait(false);

        if (defaultRow is null)
        {
            // No local global default (cloud model, unset, or non-llama): unload every
            // loaded llama alias on every instance.
            _logger.LogInformation(
                "Global default model is not a llama-cpp model; unloading all loaded llama aliases on {Count} instance(s).",
                instances.Count);
            await UnloadLoadedAliasesAsync(instances, defaultAlias: null, cancellationToken).ConfigureAwait(false);
            _notebookChatAliasState.ClearActiveChatAlias();
            return;
        }

        var defaultAlias = NormalizeRouterModelId(defaultRow.RouterModelId);
        var defaultKey = ResolveInstanceKey(defaultRow.StackBaseUrl);

        // The new default's instance keeps (or gets) its alias; every other loaded
        // alias on every instance is unloaded.
        _logger.LogInformation(
            "Global default model changed to {DefaultModelId} (alias {DefaultAlias}); reconciling {Count} instance(s).",
            defaultRow.ModelId,
            defaultAlias,
            instances.Count);

        await UnloadLoadedAliasesAsync(instances, defaultAlias, cancellationToken).ConfigureAwait(false);

        var defaultInstance = instances.FirstOrDefault(i =>
            defaultKey is not null
            && string.Equals(i.CanonicalKey, defaultKey, StringComparison.OrdinalIgnoreCase));
        if (defaultInstance is null)
        {
            _logger.LogWarning(
                "Default model {DefaultModelId} targets instance {InstanceKey} which is not in the configured universe; skipping its load.",
                defaultRow.ModelId,
                defaultKey);
            return;
        }

        var client = ClientFor(defaultRow.StackBaseUrl);
        var isLoaded = (await client.ListModelsAsync(cancellationToken).ConfigureAwait(false)).Data
            .Any(m => IsLoaded(m) && string.Equals(NormalizeRouterModelId(m.Id), defaultAlias, StringComparison.Ordinal));
        if (!isLoaded)
        {
            _logger.LogInformation("Loading default alias {Alias} on {Instance}.", defaultAlias, defaultInstance.CanonicalKey);
            await using var _ = await _coordinator.AcquireAliasLockAsync(defaultAlias, cancellationToken).ConfigureAwait(false);
            await client.LoadModelAsync(defaultAlias, cancellationToken).ConfigureAwait(false);
            await WaitForAliasAsync(client, defaultAlias, loaded: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UnloadLoadedAliasesAsync(
        IReadOnlyList<LocalAiInstance> instances,
        string? defaultAlias,
        CancellationToken cancellationToken)
    {
        foreach (var instance in instances)
        {
            var client = ClientFor(instance.Base);
            var loaded = (await client.ListModelsAsync(cancellationToken).ConfigureAwait(false)).Data
                .Where(IsLoaded)
                .Select(static m => m.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var toUnload = loaded
                .Where(alias => !string.Equals(alias, defaultAlias, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var alias in toUnload)
            {
                try
                {
                    _logger.LogInformation("Unloading alias {Alias} on {Instance}.", alias, instance.CanonicalKey);
                    await using var _ = await _coordinator.AcquireAliasLockAsync(alias, cancellationToken).ConfigureAwait(false);
                    await client.UnloadModelAsync(alias, cancellationToken).ConfigureAwait(false);
                    await WaitForAliasAsync(client, alias, loaded: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort per alias: one failed unload must not stop the rest.
                    _logger.LogWarning(ex, "Failed to unload alias {Alias} on {Instance}.", alias, instance.CanonicalKey);
                }
            }
        }
    }

    private async Task WaitForAliasAsync(
        ILlamaServerRuntimeClient client,
        string alias,
        bool loaded,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < VerifyTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var match = response.Data
                .FirstOrDefault(m => string.Equals(NormalizeRouterModelId(m.Id), alias, StringComparison.OrdinalIgnoreCase));
            if (match is null || IsLoaded(match) == loaded)
            {
                return;
            }

            await Task.Delay(VerifyPollInterval, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Timed out waiting for alias {Alias} to reach the expected state (loaded={Loaded}).",
            alias,
            loaded);
    }

    private async Task<LocalDefaultLlamaRow?> ResolveDefaultLlamaRowAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var store = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IChatDefaultsStore>();
        var defaultModelId = store.Current.DefaultModelId?.Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return null;
        }

        var row = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == defaultModelId && m.IsActive)
            .Select(m => new { m.ModelId, m.Provider, m.RuntimeConfigJson })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null
            || !string.Equals(row.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
        {
            return null;
        }

        var config = LocalRuntimeConfigurationParser.ParseRequired(row.ModelId, row.RuntimeConfigJson);
        return new LocalDefaultLlamaRow(row.ModelId, config.RouterModelId, config.StackBaseUrl);
    }

    private ILlamaServerRuntimeClient ClientFor(string? stackBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(stackBaseUrl))
        {
            return _clientProvider.Global;
        }

        var trimmed = stackBaseUrl.Trim().TrimEnd('/');
        var key = _stackApiKeysByBase.TryGetValue(trimmed, out var k) ? k : null;
        return _clientProvider.GetClientForStack(trimmed, key) ?? _clientProvider.Global;
    }

    private async Task ResolveStackApiKeysAsync(ApplicationDbContext? db, CancellationToken cancellationToken)
    {
        if (db is null)
        {
            return;
        }

        _stackApiKeysByBase.Clear();
        var rows = await db.Models
            .AsNoTracking()
            .Where(m => m.Provider == "llama-cpp" && m.IsActive && m.RuntimeConfigJson != null)
            .Select(m => new { m.ModelId, m.RuntimeConfigJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
            {
                continue;
            }

            LocalRuntimeConfiguration config;
            try
            {
                config = LocalRuntimeConfigurationParser.Parse(row.ModelId, row.RuntimeConfigJson);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(config.StackBaseUrl) && !string.IsNullOrWhiteSpace(config.StackApiKey))
            {
                _stackApiKeysByBase[config.StackBaseUrl.Trim().TrimEnd('/')] = config.StackApiKey;
            }
        }
    }

    private static string? ResolveInstanceKey(string? stackBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(stackBaseUrl))
        {
            return null;
        }

        var trimmed = stackBaseUrl.Trim().TrimEnd('/');
        try
        {
            return LocalAiStackHostResolver.CanonicalInstanceKey(trimmed);
        }
        catch (UriFormatException)
        {
            return trimmed;
        }
    }

    private static bool IsLoaded(LlamaModelData model)
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

    private static string NormalizeRouterModelId(string id) => id.Trim();

    private sealed record LocalDefaultLlamaRow(string ModelId, string RouterModelId, string StackBaseUrl);
}
