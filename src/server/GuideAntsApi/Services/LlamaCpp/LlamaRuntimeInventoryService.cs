using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.LlamaCpp;

public interface ILlamaRuntimeInventoryService
{
    Task<IReadOnlyList<LlamaRuntimeInventoryItemDto>> GetInventoryAsync(CancellationToken cancellationToken = default);
}

public sealed class LlamaRuntimeInventoryService : ILlamaRuntimeInventoryService
{
    private readonly ApplicationDbContext _context;
    private readonly IRouterModelsConfigService _routerModels;
    private readonly ILlamaServerRuntimeClient _llamaClient;
    private readonly ILogger<LlamaRuntimeInventoryService> _logger;

    public LlamaRuntimeInventoryService(
        ApplicationDbContext context,
        IRouterModelsConfigService routerModels,
        ILlamaServerRuntimeClient llamaClient,
        ILogger<LlamaRuntimeInventoryService> logger)
    {
        _context = context;
        _routerModels = routerModels;
        _llamaClient = llamaClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LlamaRuntimeInventoryItemDto>> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        var routerEntries = await _routerModels.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
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

        var catalogRows = await _context.Models
            .AsNoTracking()
            .Where(m => m.Provider == "llama-cpp")
            .Select(m => new { m.ModelId, m.LocalRuntimeJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var catalogByRouter = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in catalogRows)
        {
            if (string.IsNullOrWhiteSpace(row.LocalRuntimeJson))
            {
                continue;
            }

            try
            {
                var parsed = LocalRuntimeConfigurationParser.Parse(row.ModelId, row.LocalRuntimeJson);
                if (!catalogByRouter.TryGetValue(parsed.RouterModelId, out var list))
                {
                    list = [];
                    catalogByRouter[parsed.RouterModelId] = list;
                }

                list.Add(row.ModelId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping catalog model {ModelId} for inventory (invalid LocalRuntimeJson).", row.ModelId);
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

            var notebookCount = await CountNotebookReferencesAsync(catalogIds, cancellationToken).ConfigureAwait(false);

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
                RouterCacheRamMib: entry?.CacheRamMib));
        }

        return results;
    }

    private static string MapRuntimeState(LlamaModelData? data)
    {
        if (data is null)
        {
            return "unloaded";
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

    private Task<int> CountNotebookReferencesAsync(IReadOnlyList<string> catalogModelIds, CancellationToken cancellationToken)
    {
        if (catalogModelIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        var idSet = catalogModelIds.ToHashSet(StringComparer.Ordinal);
        return _context.Notebooks
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
