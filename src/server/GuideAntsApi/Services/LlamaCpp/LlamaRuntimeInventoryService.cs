using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GuideAntsApi.Services.LlamaCpp;

public interface ILlamaRuntimeInventoryService
{
    Task<IReadOnlyList<LlamaRuntimeInventoryItemDto>> GetInventoryAsync(CancellationToken cancellationToken = default);
}

public sealed class LlamaRuntimeInventoryService : ILlamaRuntimeInventoryService
{
    private const string InventoryCacheKey = "llama.runtime.inventory";
    private static readonly TimeSpan InventoryCacheTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InventoryFailureCacheTtl = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRouterModelsConfigService _routerModels;
    private readonly ILlamaServerRuntimeClient _llamaClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LlamaRuntimeInventoryService> _logger;

    public LlamaRuntimeInventoryService(
        IServiceScopeFactory scopeFactory,
        IRouterModelsConfigService routerModels,
        ILlamaServerRuntimeClient llamaClient,
        IMemoryCache cache,
        ILogger<LlamaRuntimeInventoryService> logger)
    {
        _scopeFactory = scopeFactory;
        _routerModels = routerModels;
        _llamaClient = llamaClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LlamaRuntimeInventoryItemDto>> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(InventoryCacheKey, out IReadOnlyList<LlamaRuntimeInventoryItemDto>? cached)
            && cached is not null)
        {
            return cached;
        }

        try
        {
            var inventory = await BuildInventoryAsync(cancellationToken).ConfigureAwait(false);
            _cache.Set(
                InventoryCacheKey,
                inventory,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = InventoryCacheTtl,
                    Size = 1
                });
            return inventory;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build llama runtime inventory.");
            _cache.Set(
                InventoryCacheKey,
                Array.Empty<LlamaRuntimeInventoryItemDto>(),
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = InventoryFailureCacheTtl,
                    Size = 1
                });
            return Array.Empty<LlamaRuntimeInventoryItemDto>();
        }
    }

    private async Task<IReadOnlyList<LlamaRuntimeInventoryItemDto>> BuildInventoryAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        IReadOnlyList<RouterModelEntry> routerEntries;
        try
        {
            routerEntries = await _routerModels.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list llama router entries; inventory will omit live router state.");
            routerEntries = [];
        }

        var routerByAlias = routerEntries.ToDictionary(e => e.Alias, StringComparer.Ordinal);

        LlamaModelsResponse llamaList;
        try
        {
            llamaList = await _llamaClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list llama runtime models; inventory will show unknown runtime state.");
            llamaList = new LlamaModelsResponse();
        }

        var runtimeById = new Dictionary<string, LlamaModelData>(StringComparer.Ordinal);
        foreach (var item in llamaList.Data)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            runtimeById[item.Id] = item;
        }

        var catalogRows = await context.Models
            .AsNoTracking()
            .Where(m => m.Provider == "llama-cpp")
            .Select(m => new { m.ModelId, m.RuntimeConfigJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var installationByModelId = await context.LocalModelInstallations
            .AsNoTracking()
            .ToDictionaryAsync(
                i => i.ModelId,
                i => new LlamaInstallationProvenanceSummaryDto(
                    i.CatalogId,
                    i.CatalogVersion,
                    i.QuantId),
                StringComparer.Ordinal,
                cancellationToken)
            .ConfigureAwait(false);

        var catalogByRouter = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in catalogRows)
        {
            if (string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
            {
                continue;
            }

            try
            {
                var parsed = LocalRuntimeConfigurationParser.Parse(row.ModelId, row.RuntimeConfigJson);
                if (!catalogByRouter.TryGetValue(parsed.RouterModelId, out var list))
                {
                    list = [];
                    catalogByRouter[parsed.RouterModelId] = list;
                }

                list.Add(row.ModelId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping catalog model {ModelId} for inventory (invalid RuntimeConfigJson).", row.ModelId);
            }
        }

        var allRouterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in routerByAlias.Keys)
        {
            allRouterIds.Add(a);
        }

        foreach (var a in catalogByRouter.Keys)
        {
            allRouterIds.Add(a);
        }

        var results = new List<LlamaRuntimeInventoryItemDto>();
        foreach (var routerId in allRouterIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            routerByAlias.TryGetValue(routerId, out var entry);
            var modelPath = entry?.ModelPath;
            var mmprojPath = entry?.MmprojPath;

            var hasModel = entry?.HasModelFile ?? !string.IsNullOrWhiteSpace(modelPath);
            var hasMmproj = entry?.HasMmprojFile ?? !string.IsNullOrWhiteSpace(mmprojPath);

            runtimeById.TryGetValue(routerId, out var runtimeRow);
            var runtimeState = MapRuntimeState(runtimeRow);

            catalogByRouter.TryGetValue(routerId, out var catalogIds);
            catalogIds ??= [];

            var notebookCount = await CountNotebookReferencesAsync(
                context,
                catalogIds,
                cancellationToken).ConfigureAwait(false);

            LlamaInstallationProvenanceSummaryDto? provenance = null;
            foreach (var catalogId in catalogIds)
            {
                if (installationByModelId.TryGetValue(catalogId, out var match))
                {
                    provenance = match;
                    break;
                }
            }

            results.Add(new LlamaRuntimeInventoryItemDto(
                RouterModelId: routerId,
                RuntimeState: runtimeState,
                ModelPath: modelPath,
                MmprojPath: mmprojPath,
                HasModelFile: hasModel,
                HasMmprojFile: hasMmproj,
                CatalogModelIds: catalogIds,
                NotebookReferenceCount: notebookCount,
                RouterContextSize: entry?.ContextSize,
                RouterCacheRamMib: entry?.CacheRamMib,
                RouterPreset: entry?.Preset,
                RuntimeFailed: runtimeRow?.Failed ?? false,
                RuntimeExitCode: runtimeRow?.ExitCode,
                InstallationProvenance: provenance));
        }

        return results;
    }

    internal static string MapRuntimeState(LlamaModelData? data)
    {
        if (data is null)
        {
            return "unloaded";
        }

        if (data.Failed)
        {
            return "failed";
        }

        if (!string.IsNullOrWhiteSpace(data.Status?.Value))
        {
            return data.Status.Value.ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(data.State))
        {
            return data.State.ToLowerInvariant();
        }

        return "unknown";
    }

    private static Task<int> CountNotebookReferencesAsync(
        ApplicationDbContext context,
        IReadOnlyList<string> catalogModelIds,
        CancellationToken cancellationToken)
    {
        if (catalogModelIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        var idSet = catalogModelIds.ToHashSet(StringComparer.Ordinal);
        return context.Notebooks
            .AsNoTracking()
            .Where(n => n.Guide != null && (
                (n.Guide!.ModelId != null && idSet.Contains(n.Guide.ModelId)) ||
                n.Guide.CrewMembers.Any(cm =>
                    cm.Assistant != null &&
                    cm.Assistant.ModelId != null &&
                    idSet.Contains(cm.Assistant.ModelId))))
            .CountAsync(cancellationToken);
    }
}
