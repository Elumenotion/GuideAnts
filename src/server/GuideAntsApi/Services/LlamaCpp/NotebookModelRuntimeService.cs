using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.LlamaCpp;

public class NotebookModelRuntimeService : INotebookModelRuntimeService
{
    /// <summary>
    /// Synthetic operation id returned when models are loading outside of
    /// <see cref="_operations"/> (startup warmup or settings-UI load).
    /// </summary>
    public const string ExternalLoadingOperationId = "__external_loading__";

    private readonly ApplicationDbContext _context;
    private readonly ILlamaServerRuntimeClient _llamaClient;
    private readonly IMemoryCache _cache;
    private readonly ILlamaRuntimeCoordinator _coordinator;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly ILocalAiStartupWarmupService _localAiWarmupService;
    private readonly ILocalAiWarmupService _localAiWarmup;
    private readonly INotebookChatAliasState _notebookChatAliasState;
    private readonly ILocalAiStackHostResolver _stackHostResolver;
    private readonly ILlamaStackRuntimeClientProvider _stackClients;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotebookModelRuntimeService> _logger;

    // Singleton state for operations. In a multi-node deployment, this would need to be distributed.
    private static readonly ConcurrentDictionary<string, ModelLoadOperationDto> _operations = new();
    private static readonly SemaphoreSlim _loadLock = new(1, 1);
    private const string RouterModelsCacheKey = "llama.runtime.router-models";
    // Router model state is generally stable; keep a longer cache window and invalidate on explicit load/unload operations.
    private static readonly TimeSpan RouterModelsCacheTtl = TimeSpan.FromMinutes(5);

    public NotebookModelRuntimeService(
        ApplicationDbContext context,
        ILlamaServerRuntimeClient llamaClient,
        IMemoryCache cache,
        ILlamaRuntimeCoordinator coordinator,
        IChatModelResolver chatModelResolver,
        ILocalAiStartupWarmupService localAiWarmupService,
        ILocalAiWarmupService localAiWarmup,
        INotebookChatAliasState notebookChatAliasState,
        ILocalAiStackHostResolver stackHostResolver,
        ILlamaStackRuntimeClientProvider stackClients,
        IServiceScopeFactory scopeFactory,
        ILogger<NotebookModelRuntimeService> logger)
    {
        _context = context;
        _llamaClient = llamaClient;
        _cache = cache;
        _coordinator = coordinator;
        _chatModelResolver = chatModelResolver;
        _localAiWarmupService = localAiWarmupService;
        _localAiWarmup = localAiWarmup;
        _notebookChatAliasState = notebookChatAliasState;
        _stackHostResolver = stackHostResolver;
        _stackClients = stackClients;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<NotebookLlamaRuntimeStatusDto> GetRuntimeStatusAsync(Guid notebookId, Guid? assistantId = null, CancellationToken cancellationToken = default)
    {
        var notebook = await _context.Notebooks
            .Include(n => n.Guide)
                .ThenInclude(g => g!.CrewMembers)
                    .ThenInclude(cm => cm.Assistant)
            .FirstOrDefaultAsync(n => n.Id == notebookId, cancellationToken);

        if (notebook == null)
            throw new ArgumentException("Notebook not found", nameof(notebookId));

        List<ModelDto> requiredModels;
        try
        {
            requiredModels = await GetRequiredLlamaModelsAsync(notebook, assistantId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new NotebookLlamaRuntimeStatusDto
            {
                State = "invalid",
                SelectedAssistantId = assistantId,
                Conflicts = { ex.Message }
            };
        }
        
        var status = new NotebookLlamaRuntimeStatusDto
        {
            SelectedAssistantId = assistantId,
            RequiredModels = requiredModels
        };

        // Short-circuit if no local models are required
        if (!requiredModels.Any(m => m.RuntimeConfig != null))
        {
            status.State = "ready";
            return status;
        }

        // Check current per-instance runtime state.
        try
        {
            // Runtime readiness is used as a hard preflight gate before chat dispatch.
            // It must reflect live per-instance state, not the long-lived 5-minute cache,
            // otherwise we can return "ready" and then hit upstream 400 "model is not loaded".
            var instances = await GetInstanceSnapshotsAsync(useCache: false, cancellationToken);

            var loadedByInstance = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in instances)
            {
                loadedByInstance[instance.CanonicalKey] = instance.Snapshot.Data
                    .Where(IsRouterModelLoaded)
                    .Select(d => NormalizeRouterModelId(d.Id))
                    .ToHashSet(StringComparer.Ordinal);
            }

            var allModels = await GetLlamaModelsFromCatalogAsync(cancellationToken);
            status.LoadedModels = allModels
                .Where(m => m.RuntimeConfig != null
                    && !string.IsNullOrWhiteSpace(m.RuntimeConfig.RouterModelId)
                    && instances.Any(i => loadedByInstance[i.CanonicalKey].Contains(NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId!))))
                .ToList();

            var requiredRouterIds = requiredModels
                .Where(m => m.RuntimeConfig != null && !string.IsNullOrWhiteSpace(m.RuntimeConfig.RouterModelId))
                .Select(m => NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId!))
                .ToHashSet(StringComparer.Ordinal);

            // Per-instance failure: a required row's alias failed on the instance that owns it.
            foreach (var requiredModel in requiredModels.Where(m => m.RuntimeConfig is not null))
            {
                var requiredRouterId = NormalizeRouterModelId(requiredModel.RuntimeConfig!.RouterModelId!);
                var instanceKey = ResolveInstanceBase(requiredModel);
                var failedRequiredModels = instances
                    .Where(i => string.Equals(i.CanonicalKey, instanceKey, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(i => i.Snapshot.Data)
                    .Where(m => requiredRouterIds.Contains(NormalizeRouterModelId(m.Id)) && IsRouterModelFailed(m))
                    .ToList();
                if (failedRequiredModels.Count > 0)
                {
                    status.State = "failed";
                    foreach (var failedModel in failedRequiredModels)
                    {
                        var failure = DescribeRouterModelFailure(failedModel);
                        if (!string.IsNullOrWhiteSpace(failure))
                        {
                            status.Conflicts.Add(failure);
                        }
                    }

                    status.ActiveOperation = new ModelLoadOperationDto
                    {
                        OperationId = ExternalLoadingOperationId,
                        State = "failed",
                        StartedAt = DateTime.UtcNow,
                        CompletedAt = DateTime.UtcNow,
                        ErrorDetails = status.Conflicts.FirstOrDefault()
                            ?? "One or more required local models failed to load."
                    };
                    return status;
                }
            }

            // Per-instance readiness: every required alias must be loaded on the instance
            // that owns it. Instances are independent - one instance's load never
            // satisfies another's requirement.
            var requiredByInstance = GroupRequiredRouterIdsByInstance(requiredModels);
            var isReady = requiredByInstance.All(pair =>
                loadedByInstance.TryGetValue(pair.Key, out var loaded)
                && pair.Value.IsSubsetOf(loaded));
            if (isReady)
            {
                // Treat "required models are already loaded" as the highest-priority truth.
                // We intentionally prefer this over an in-flight operation marker because
                // operation state can remain "loading" while auxiliary services are still
                // warming up, and chat should not be blocked in that phase.
                status.State = "ready";
            }
            else if (IsExternalLoadInProgress(requiredByInstance, instances))
            {
                status.State = "loading";
                status.ActiveOperation = CreateExternalLoadingOperation(
                    _localAiWarmupService.IsWarmupInProgress
                        ? "loading"
                        : ResolveExternalLoadPhase(instances, requiredByInstance));
            }
            else
            {
                // Check if any active operation
                var activeOp = _operations.Values.FirstOrDefault(o => o.State == "queued" || o.State == "unloading" || o.State == "loading" || o.State == "verifying");
                if (activeOp != null)
                {
                    status.State = "loading";
                    status.ActiveOperation = activeOp;
                }
                else
                {
                    status.State = "requires_load";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get per-instance router state");
            status.State = "failed";
            status.Conflicts.Add("Failed to communicate with local llama servers.");
        }

        return status;
    }

    public async Task<ModelLoadOperationDto> StartLoadOperationAsync(Guid notebookId, Guid? assistantId = null, CancellationToken cancellationToken = default)
    {
        var status = await GetRuntimeStatusAsync(notebookId, assistantId, cancellationToken);
        
        if (status.State == "invalid")
            throw new InvalidOperationException("Cannot load incompatible models: " + string.Join(", ", status.Conflicts));

        if (status.State == "ready")
        {
            return new ModelLoadOperationDto
            {
                OperationId = Guid.NewGuid().ToString(),
                State = "ready",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                TargetAssistantId = assistantId
            };
        }

        if (status.State == "loading" && status.ActiveOperation != null)
            return status.ActiveOperation;

        var op = new ModelLoadOperationDto
        {
            OperationId = Guid.NewGuid().ToString(),
            State = "queued",
            StartedAt = DateTime.UtcNow,
            TargetAssistantId = assistantId
        };

        _operations[op.OperationId] = op;

        // Fire and forget the actual load process
        _ = Task.Run(async () => await ProcessLoadOperationAsync(op, status.RequiredModels));

        return op;
    }

    public async Task<ModelLoadOperationDto?> GetOperationStatusAsync(Guid notebookId, string operationId, CancellationToken cancellationToken = default)
    {
        if (_operations.TryGetValue(operationId, out var op))
        {
            return op;
        }

        if (!string.Equals(operationId, ExternalLoadingOperationId, StringComparison.Ordinal))
        {
            return null;
        }

        var status = await GetRuntimeStatusAsync(notebookId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (status.State == "ready")
        {
            return new ModelLoadOperationDto
            {
                OperationId = operationId,
                State = "ready",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
        }

        if (status.ActiveOperation != null
            && (status.State == "loading" || status.State == "failed"))
        {
            return status.ActiveOperation;
        }

        return new ModelLoadOperationDto
        {
            OperationId = operationId,
            State = "failed",
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            ErrorDetails = status.Conflicts.FirstOrDefault() ?? "External model load did not complete."
        };
    }

    public async Task<ModelLoadOperationDto> StartUnloadForNotebookContextAsync(
        Guid notebookId,
        Guid? assistantId = null,
        CancellationToken cancellationToken = default)
    {
        var notebook = await _context.Notebooks
            .Include(n => n.Guide)
                .ThenInclude(g => g!.CrewMembers)
                    .ThenInclude(cm => cm.Assistant)
            .FirstOrDefaultAsync(n => n.Id == notebookId, cancellationToken)
            .ConfigureAwait(false);

        if (notebook == null)
        {
            throw new ArgumentException("Notebook not found", nameof(notebookId));
        }

        var op = new ModelLoadOperationDto
        {
            OperationId = Guid.NewGuid().ToString(),
            State = "queued",
            StartedAt = DateTime.UtcNow,
            TargetAssistantId = assistantId
        };
        _operations[op.OperationId] = op;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _loadLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    op.State = "unloading";
                    InvalidateRouterModelsCache();

                    // Forget this context's per-instance notebook aliases so the next
                    // plan apply emits enabled:false for the instances it loaded on.
                    List<ModelDto> requiredModels;
                    try
                    {
                        requiredModels = await GetRequiredLlamaModelsAsync(notebook, assistantId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        requiredModels = new List<ModelDto>();
                    }

                    // Direct actuation: unload this context's aliases on the instances it
                    // loaded them on (within-instance only). No lifecycle-plan apply - a
                    // plan apply would send an unload for llama and the container would
                    // act on it, which must not happen.
                    var instances = await GetInstanceSnapshotsAsync(useCache: false, CancellationToken.None).ConfigureAwait(false);
                    var instanceByKey = instances.ToDictionary(i => i.CanonicalKey, StringComparer.OrdinalIgnoreCase);
                    foreach (var model in requiredModels.Where(m => m.RuntimeConfig is not null
                        && !string.IsNullOrWhiteSpace(m.RuntimeConfig.RouterModelId)))
                    {
                        var alias = NormalizeRouterModelId(model.RuntimeConfig!.RouterModelId!);
                        var instanceBase = ResolveInstanceBase(model);
                        if (instanceByKey.TryGetValue(instanceBase, out var instance))
                        {
                            var loaded = instance.Snapshot.Data.Any(m =>
                                IsRouterModelLoaded(m)
                                && string.Equals(NormalizeRouterModelId(m.Id), alias, StringComparison.Ordinal));
                            if (loaded)
                            {
                                await using var _ = await _coordinator.AcquireAliasLockAsync(alias, CancellationToken.None).ConfigureAwait(false);
                                await instance.Client.UnloadModelAsync(alias, CancellationToken.None).ConfigureAwait(false);
                            }
                        }

                        _notebookChatAliasState.ClearInstance(instanceBase);
                    }

                    op.State = "ready";
                    op.CompletedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notebook llama unload failed");
                    op.State = "failed";
                    op.ErrorDetails = ex.Message;
                    op.CompletedAt = DateTime.UtcNow;
                }
                finally
                {
                    _loadLock.Release();
                }
            },
            CancellationToken.None);

        return op;
    }

    private async Task ProcessLoadOperationAsync(ModelLoadOperationDto op, List<ModelDto> requiredModels)
    {
        var warmup = _localAiWarmup;
        var drainedAuxForChat = false;

        try
        {
            await _loadLock.WaitAsync();
            InvalidateRouterModelsCache();

            // Llama actuation is API-direct: the load path loads the required alias on
            // its own instance and evicts the within-instance conflicts itself below.
            // No lifecycle-plan apply on a load path - a plan apply would send an unload
            // for llama (the plan no longer carries llama intent) and the container would
            // act on it, which must not happen for loads.

            // Per-instance reconcile (rule 4/5): on each instance that owns required
            // rows, evict loaded-but-not-required aliases (within that instance only),
            // load the missing ones, and verify. Other instances are untouched -
            // the plan apply below carries their sections unchanged.
            var requiredByInstance = GroupRequiredRouterIdsByInstance(requiredModels);
            var requiredModelIds = requiredModels
                .Where(m => m.RuntimeConfig != null && !string.IsNullOrWhiteSpace(m.RuntimeConfig.RouterModelId))
                .Select(m => NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId!))
                .ToHashSet(StringComparer.Ordinal);

            var instances = await GetInstanceSnapshotsAsync(useCache: false, CancellationToken.None);
            var instanceByKey = instances
                .ToDictionary(i => i.CanonicalKey, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in requiredByInstance)
            {
                var requiredIds = pair.Value;
                if (!instanceByKey.TryGetValue(pair.Key, out var instance))
                {
                    throw new InvalidOperationException(
                        $"Instance '{pair.Key}' is not in the configured stack universe.");
                }

                var loadedIds = instance.Snapshot.Data
                    .Where(IsRouterModelLoaded)
                    .Select(d => NormalizeRouterModelId(d.Id))
                    .ToHashSet(StringComparer.Ordinal);

                var toUnload = loadedIds.Except(requiredModelIds).ToList();
                if (toUnload.Count > 0)
                {
                    op.State = "unloading";
                    foreach (var id in toUnload)
                    {
                        await using var _ = await _coordinator.AcquireAliasLockAsync(id, CancellationToken.None);
                        await instance.Client.UnloadModelAsync(id, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                var toLoad = requiredModels
                    .Where(m => m.RuntimeConfig != null
                        && !string.IsNullOrWhiteSpace(m.RuntimeConfig.RouterModelId)
                        && string.Equals(ResolveInstanceBase(m), pair.Key, StringComparison.OrdinalIgnoreCase)
                        && !loadedIds.Contains(NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId!)))
                    .ToList();
                if (toLoad.Count > 0)
                {
                    op.State = "loading";
                    foreach (var model in toLoad)
                    {
                        var routerModelId = NormalizeRouterModelId(model.RuntimeConfig!.RouterModelId!);
                        await using var _ = await _coordinator.AcquireAliasLockAsync(routerModelId, CancellationToken.None);
                        await instance.Client.LoadModelAsync(routerModelId, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                // Record the per-instance notebook alias so subsequent lifecycle applies
                // keep this instance's section enabled with this alias.
                foreach (var id in requiredIds)
                {
                    _notebookChatAliasState.SetActiveChatAliasForInstance(pair.Key, id);
                }
            }

            op.State = "verifying";
            if (requiredByInstance.Count > 0)
            {
                var verifyStartedAt = DateTime.UtcNow;
                var verifyTimeout = TimeSpan.FromMinutes(5);
                var pollInterval = TimeSpan.FromSeconds(2);
                var isReady = false;
                var missingByInstance = new List<string>();

                while (DateTime.UtcNow - verifyStartedAt < verifyTimeout)
                {
                    var freshInstances = await GetInstanceSnapshotsAsync(useCache: false, CancellationToken.None);
                    missingByInstance = freshInstances
                        .Where(i => requiredByInstance.TryGetValue(i.CanonicalKey, out var requiredIds)
                            && !requiredIds.IsSubsetOf(i.Snapshot.Data
                                .Where(IsRouterModelLoaded)
                                .Select(d => NormalizeRouterModelId(d.Id))))
                        .Select(i => $"{i.CanonicalKey}: {string.Join(", ",
                            requiredByInstance[i.CanonicalKey].Except(i.Snapshot.Data
                                .Where(IsRouterModelLoaded)
                                .Select(d => NormalizeRouterModelId(d.Id))))}")
                        .ToList();
                    if (missingByInstance.Count == 0)
                    {
                        isReady = true;
                        break;
                    }

                    await Task.Delay(pollInterval);
                }

                if (!isReady)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for local models to report loaded. Missing: {string.Join("; ", missingByInstance)}");
                }
            }

            op.State = "ready";
            op.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load operation failed");
            op.State = "failed";
            op.ErrorDetails = ex.Message;
            op.CompletedAt = DateTime.UtcNow;
        }
        finally
        {
            if (drainedAuxForChat
                && !string.Equals(op.State, "ready", StringComparison.Ordinal))
            {
                try
                {
                    _logger.LogInformation(
                        "Load operation {OperationId} failed after auxiliary drain; restoring routed warmup desired state.",
                        op.OperationId);
                    await warmup.SyncDesiredAndApplyAsync(
                        waitForCompletion: false,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception reloadEx)
                {
                    _logger.LogError(
                        reloadEx,
                        "Failed restoring warmup desired state after operation {OperationId} failure.",
                        op.OperationId);
                }
            }

            _loadLock.Release();
        }
    }

    /// <summary>
    /// A live snapshot of every configured llama instance (global + row-owned).
    /// </summary>
    private sealed record InstanceSnapshot(string CanonicalKey, ILlamaServerRuntimeClient Client, LlamaModelsResponse Snapshot);

    private async Task<List<InstanceSnapshot>> GetInstanceSnapshotsAsync(bool useCache, CancellationToken cancellationToken)
    {
        // Deduplicated by canonical identity: one physical box = one snapshot/client.
        var instances = await _stackHostResolver.GetAllConfiguredInstancesAsync(cancellationToken).ConfigureAwait(false);
        var globalCanonical = _stackHostResolver.GetStackBaseForService(LocalAiStackHostUrls.LlamaServiceId) is { } gb
            ? LocalAiStackHostResolver.CanonicalInstanceKey(gb)
            : null;
        var snapshots = new List<InstanceSnapshot>(instances.Count);
        foreach (var instance in instances)
        {
            var isGlobal = globalCanonical is not null
                && string.Equals(instance.CanonicalKey, globalCanonical, StringComparison.OrdinalIgnoreCase);
            var client = isGlobal
                ? _llamaClient
                : (_stackClients.GetClientForStack(instance.Base, stackApiKey: null) ?? _llamaClient);
            LlamaModelsResponse snapshot;
            if (useCache && isGlobal)
            {
                snapshot = await GetRouterModelsAsync(useCache: true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            }

            snapshots.Add(new InstanceSnapshot(instance.CanonicalKey, client, snapshot));
        }

        return snapshots;
    }

    private string ResolveInstanceBase(ModelDto model)
    {
        // Key everything by CANONICAL identity (host resolved to IP:port) so a row base,
        // a resolver base, and a snapshot key all share one identity even when the same
        // physical box is configured under several host names. Rows without a row-owned
        // stack target the global LlamaCpp:BaseUrl.
        if (string.IsNullOrWhiteSpace(model.RuntimeConfig?.StackBaseUrl))
        {
            var globalBase = _stackHostResolver.GetStackBaseForService(LocalAiStackHostUrls.LlamaServiceId);
            return globalBase is null
                ? string.Empty
                : LocalAiStackHostResolver.CanonicalInstanceKey(globalBase);
        }

        var normalized = LocalAiStackHostUrls.NormalizeStackBaseUrl(model.RuntimeConfig.StackBaseUrl)
            ?? model.RuntimeConfig.StackBaseUrl.TrimEnd('/');
        return LocalAiStackHostResolver.CanonicalInstanceKey(normalized);
    }

    private Dictionary<string, HashSet<string>> GroupRequiredRouterIdsByInstance(List<ModelDto> requiredModels)
    {
        var byInstance = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in requiredModels.Where(m => m.RuntimeConfig is not null))
        {
            var instanceKey = ResolveInstanceBase(model);
            if (!byInstance.TryGetValue(instanceKey, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                byInstance[instanceKey] = set;
            }

            set.Add(NormalizeRouterModelId(model.RuntimeConfig!.RouterModelId!));
        }

        return byInstance;
    }

    private async Task<LlamaModelsResponse> GetRouterModelsAsync(bool useCache, CancellationToken cancellationToken)
    {
        if (useCache && _cache.TryGetValue<LlamaModelsResponse>(RouterModelsCacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var fresh = await _llamaClient.ListModelsAsync(cancellationToken);
        _cache.Set(
            RouterModelsCacheKey,
            fresh,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = RouterModelsCacheTtl,
                Size = 1
            });
        return fresh;
    }

    private void InvalidateRouterModelsCache()
    {
        _cache.Remove(RouterModelsCacheKey);
    }

    private async Task<List<ModelDto>> GetLlamaModelsFromCatalogAsync(CancellationToken cancellationToken)
    {
        var models = await _context.Models
            .Where(m => m.Provider == "llama-cpp" && m.IsActive)
            .Select(m => new
            {
                m.ModelId,
                m.DisplayName,
                m.Description,
                m.ReasoningChoicesJson,
                m.IsActive,
                m.DisplayOrder,
                m.RuntimeConfigJson
            })
            .ToListAsync(cancellationToken);

        return models.Select(m => new ModelDto(
            m.ModelId,
            m.DisplayName,
            m.Description,
            m.ReasoningChoicesJson,
            m.IsActive,
            m.DisplayOrder,
            string.IsNullOrEmpty(m.RuntimeConfigJson)
                ? null
                : ToLocalRuntimeDescriptor(m.ModelId, m.RuntimeConfigJson),
            SamplingParameterPolicy: null,
            ReasoningChoices: null,
            DefaultReasoningChoice: null
        )).ToList();
    }

    private async Task<List<ModelDto>> GetRequiredLlamaModelsAsync(Notebook notebook, Guid? assistantId, CancellationToken cancellationToken)
    {
        // R-1.6 / R-9.1: go through IChatModelResolver for every entity-level model id so the
        // preload path is identical to the dispatch path. Without this the notebook would
        // pre-load the assistant's stored ModelId even when OverrideAllChatModels=true or when
        // the assistant has no ModelId set (defaulted-to case), silently contradicting the
        // user's Settings → Overview → Default Chat Model choice.
        var candidateEntityModelIds = new List<string?>();

        candidateEntityModelIds.Add(notebook.Guide?.ModelId);

        if (notebook.Guide?.CrewMembers != null)
        {
            foreach (var member in notebook.Guide.CrewMembers)
            {
                candidateEntityModelIds.Add(member.Assistant?.ModelId);
            }
        }

        if (assistantId.HasValue)
        {
            var assistant = await _context.Assistants.FindAsync(new object[] { assistantId.Value }, cancellationToken);
            candidateEntityModelIds.Add(assistant?.ModelId);
        }

        var resolvedModelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityId in candidateEntityModelIds)
        {
            try
            {
                var resolved = _chatModelResolver.Resolve(entityId);
                if (!string.IsNullOrWhiteSpace(resolved.ModelId))
                {
                    resolvedModelIds.Add(resolved.ModelId);
                }
            }
            catch (RoutingException)
            {
                // Empty entity id + no default configured: no model to preload for this
                // candidate. Surface the real error at dispatch time rather than blocking
                // notebook open; preload is best-effort.
            }
        }

        var allLlamaModels = await GetLlamaModelsFromCatalogAsync(cancellationToken);
        return allLlamaModels.Where(m => resolvedModelIds.Contains(m.ModelId)).ToList();
    }

    private string NormalizeRouterModelId(string routerModelId)
    {
        return routerModelId;
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

    private static bool IsRouterModelLoading(LlamaModelData model)
    {
        if (!string.IsNullOrWhiteSpace(model.Status?.Value))
        {
            return string.Equals(model.Status.Value, "loading", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(model.State))
        {
            return string.Equals(model.State, "loading", StringComparison.OrdinalIgnoreCase);
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

    private static string DescribeRouterModelFailure(LlamaModelData model)
    {
        var exitCodeSuffix = model.ExitCode.HasValue
            ? $" (exit code {model.ExitCode.Value})"
            : string.Empty;
        return $"Local model '{model.Id}' failed to load{exitCodeSuffix}. Check guideants-ai logs for details.";
    }

    private bool IsExternalLoadInProgress(
        Dictionary<string, HashSet<string>> requiredByInstance,
        List<InstanceSnapshot> instances)
    {
        if (instances.Any(i => i.Snapshot.Data.Any(m =>
                requiredByInstance.TryGetValue(i.CanonicalKey, out var requiredIds)
                && requiredIds.Contains(NormalizeRouterModelId(m.Id))
                && IsRouterModelFailed(m))))
        {
            return false;
        }

        if (_localAiWarmupService.IsWarmupInProgress
            && requiredByInstance.Any(pair =>
                instances.Any(i => string.Equals(i.CanonicalKey, pair.Key, StringComparison.OrdinalIgnoreCase)
                    && !pair.Value.IsSubsetOf(i.Snapshot.Data
                        .Where(IsRouterModelLoaded)
                        .Select(m => NormalizeRouterModelId(m.Id))
                        .ToHashSet(StringComparer.Ordinal)))))
        {
            return true;
        }

        foreach (var pair in requiredByInstance)
        {
            foreach (var id in pair.Value)
            {
                if (_coordinator.IsAliasLocked(id))
                {
                    return true;
                }
            }
        }

        return instances.Any(i => i.Snapshot.Data.Any(m =>
            requiredByInstance.TryGetValue(i.CanonicalKey, out var requiredIds)
            && requiredIds.Contains(NormalizeRouterModelId(m.Id))
            && IsRouterModelLoading(m)));
    }

    private static string ResolveExternalLoadPhase(
        List<InstanceSnapshot> instances,
        Dictionary<string, HashSet<string>> requiredByInstance)
    {
        var hasLoading = instances.Any(i => i.Snapshot.Data.Any(m =>
            requiredByInstance.TryGetValue(i.CanonicalKey, out var requiredIds)
            && requiredIds.Contains(m.Id)
            && IsRouterModelLoading(m)));
        return hasLoading ? "loading" : "queued";
    }

    private static ModelLoadOperationDto CreateExternalLoadingOperation(string phase)
    {
        return new ModelLoadOperationDto
        {
            OperationId = ExternalLoadingOperationId,
            State = phase,
            StartedAt = DateTime.UtcNow
        };
    }

    private ModelRuntimeConfigDto ToLocalRuntimeDescriptor(string modelId, string runtimeConfigJson)
    {
        var parsed = LocalRuntimeConfigurationParser.Parse(modelId, runtimeConfigJson);
        return new ModelRuntimeConfigDto(parsed.RouterModelId, parsed.StackBaseUrl);
    }
}
