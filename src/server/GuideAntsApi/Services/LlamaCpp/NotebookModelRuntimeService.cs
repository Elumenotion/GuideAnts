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
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;
    private readonly IMemoryCache _cache;
    private readonly ILlamaRuntimeCoordinator _coordinator;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly ILocalAiStartupWarmupService _localAiWarmupService;
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
        IRuntimeProfileResolver runtimeProfileResolver,
        IMemoryCache cache,
        ILlamaRuntimeCoordinator coordinator,
        IChatModelResolver chatModelResolver,
        ILocalAiStartupWarmupService localAiWarmupService,
        ILogger<NotebookModelRuntimeService> logger)
    {
        _context = context;
        _llamaClient = llamaClient;
        _runtimeProfileResolver = runtimeProfileResolver;
        _cache = cache;
        _coordinator = coordinator;
        _chatModelResolver = chatModelResolver;
        _localAiWarmupService = localAiWarmupService;
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

        // Check current router state
        try
        {
            // Runtime readiness is used as a hard preflight gate before chat dispatch.
            // It must reflect live llama-router state, not the long-lived 5-minute cache,
            // otherwise we can return "ready" and then hit upstream 400 "model is not loaded".
            var routerState = await GetRouterModelsAsync(useCache: false, cancellationToken);
            var loadedRouterIds = routerState.Data
                .Where(IsRouterModelLoaded)
                .Select(d => NormalizeRouterModelId(d.Id))
                .ToHashSet();

            var allModels = await GetLlamaModelsFromCatalogAsync(cancellationToken);
            status.LoadedModels = allModels
                .Where(m => m.RuntimeConfig != null && loadedRouterIds.Contains(NormalizeRouterModelId(m.RuntimeConfig.RouterModelId)))
                .ToList();

            var requiredRouterIds = requiredModels
                .Where(m => m.RuntimeConfig != null)
                .Select(m => NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId))
                .ToHashSet();

            var failedRequiredModels = routerState.Data
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

            // Treat "required models are already loaded" as the highest-priority truth.
            // We intentionally prefer this over an in-flight operation marker because
            // operation state can remain "loading" while auxiliary services are still
            // warming up, and chat should not be blocked in that phase.
            if (requiredRouterIds.IsSubsetOf(loadedRouterIds))
            {
                status.State = "ready";
            }
            else if (IsExternalLoadInProgress(requiredRouterIds, routerState))
            {
                status.State = "loading";
                status.ActiveOperation = CreateExternalLoadingOperation(
                    _localAiWarmupService.IsWarmupInProgress
                        ? "loading"
                        : ResolveExternalLoadPhase(routerState, requiredRouterIds));
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
                else if (IsExternalLoadInProgress(requiredRouterIds, routerState))
                {
                    status.State = "loading";
                    status.ActiveOperation = CreateExternalLoadingOperation(
                        _localAiWarmupService.IsWarmupInProgress
                            ? "loading"
                            : ResolveExternalLoadPhase(routerState, requiredRouterIds));
                }
                else
                {
                    status.State = "requires_load";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get router state");
            status.State = "failed";
            status.Conflicts.Add("Failed to communicate with local llama server.");
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

        List<ModelDto> requiredModels;
        try
        {
            requiredModels = await GetRequiredLlamaModelsAsync(notebook, assistantId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("Cannot determine models to unload: " + ex.Message, ex);
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

                    var requiredRouterIds = requiredModels
                        .Where(m => m.RuntimeConfig != null)
                        .Select(m => NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId))
                        .ToHashSet();

                    foreach (var id in requiredRouterIds)
                    {
                        await using var _ = await _coordinator.AcquireAliasLockAsync(id, CancellationToken.None)
                            .ConfigureAwait(false);
                        await _llamaClient.UnloadModelAsync(id, CancellationToken.None).ConfigureAwait(false);
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
        var auxiliaryServicesWereUnloaded = false;

        try
        {
            await _loadLock.WaitAsync();
            InvalidateRouterModelsCache();

            // Guarantee LLM-first GPU ownership during model switches by
            // draining non-chat local services before changing router aliases.
            op.State = "unloading";
            await _localAiWarmupService.UnloadAuxiliaryServicesAsync(CancellationToken.None).ConfigureAwait(false);
            auxiliaryServicesWereUnloaded = true;
            
            var requiredRouterIds = requiredModels
                .Where(m => m.RuntimeConfig != null)
                .Select(m => NormalizeRouterModelId(m.RuntimeConfig!.RouterModelId))
                .ToHashSet();

            var routerStateAtStart = await GetRouterModelsAsync(useCache: false, CancellationToken.None);
            var loadedRouterIds = routerStateAtStart.Data
                .Where(IsRouterModelLoaded)
                .Select(d => NormalizeRouterModelId(d.Id))
                .ToHashSet();

            var toUnload = loadedRouterIds.Except(requiredRouterIds).ToList();
            var toLoad = requiredModels
                .Where(m => m.RuntimeConfig != null && !loadedRouterIds.Contains(NormalizeRouterModelId(m.RuntimeConfig.RouterModelId)))
                .ToList();

            if (toUnload.Any())
            {
                op.State = "unloading";
                foreach (var id in toUnload)
                {
                    // R-12.10: per-alias coordinator lock prevents the settings-UI from
                    // racing an unload while we're serving this notebook's load request.
                    await using var _unloadLock = await _coordinator.AcquireAliasLockAsync(id, CancellationToken.None);
                    await _llamaClient.UnloadModelAsync(id);
                }
            }

            if (toLoad.Any())
            {
                op.State = "loading";
                foreach (var model in toLoad)
                {
                    var routerModelId = NormalizeRouterModelId(model.RuntimeConfig!.RouterModelId);
                    await using var _loadAliasLock = await _coordinator.AcquireAliasLockAsync(routerModelId, CancellationToken.None);
                    await _llamaClient.LoadModelAsync(routerModelId, model.RuntimeConfig.LoadParams);
                }
            }

            op.State = "verifying";
            if (requiredRouterIds.Count > 0)
            {
                var verifyStartedAt = DateTime.UtcNow;
                var verifyTimeout = TimeSpan.FromMinutes(5);
                var pollInterval = TimeSpan.FromSeconds(2);
                var isReady = false;
                List<string> missingRouterIds = [];

                while (DateTime.UtcNow - verifyStartedAt < verifyTimeout)
                {
                    var routerState = await GetRouterModelsAsync(useCache: false, CancellationToken.None);
                    var loadedNow = routerState.Data
                        .Where(IsRouterModelLoaded)
                        .Select(d => NormalizeRouterModelId(d.Id))
                        .ToHashSet();

                    missingRouterIds = requiredRouterIds.Except(loadedNow).ToList();
                    if (missingRouterIds.Count == 0)
                    {
                        isReady = true;
                        break;
                    }

                    await Task.Delay(pollInterval);
                }

                if (!isReady)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for local models to report loaded. Missing: {string.Join(", ", missingRouterIds)}");
                }
            }

            // Keep ASR/Emb/TTS/SD hot after a successful LLM switch.
            op.State = "loading";
            await _localAiWarmupService.EnsureAuxiliaryServicesLoadedAsync(CancellationToken.None).ConfigureAwait(false);

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
            if (auxiliaryServicesWereUnloaded
                && !string.Equals(op.State, "ready", StringComparison.Ordinal))
            {
                try
                {
                    _logger.LogInformation(
                        "Load operation {OperationId} failed after auxiliary unload; reloading auxiliary services.",
                        op.OperationId);
                    await _localAiWarmupService.EnsureAuxiliaryServicesLoadedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception reloadEx)
                {
                    _logger.LogError(
                        reloadEx,
                        "Failed reloading auxiliary local services after operation {OperationId} failure.",
                        op.OperationId);
                }
            }

            _loadLock.Release();
        }
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

    private bool IsExternalLoadInProgress(HashSet<string> requiredRouterIds, LlamaModelsResponse routerState)
    {
        if (routerState.Data.Any(m =>
                requiredRouterIds.Contains(NormalizeRouterModelId(m.Id))
                && IsRouterModelFailed(m)))
        {
            return false;
        }

        if (_localAiWarmupService.IsWarmupInProgress)
        {
            return true;
        }

        foreach (var id in requiredRouterIds)
        {
            if (_coordinator.IsAliasLocked(id))
            {
                return true;
            }
        }

        return routerState.Data.Any(m =>
            requiredRouterIds.Contains(NormalizeRouterModelId(m.Id))
            && IsRouterModelLoading(m));
    }

    private static string ResolveExternalLoadPhase(LlamaModelsResponse routerState, HashSet<string> requiredRouterIds)
    {
        var hasLoading = routerState.Data.Any(m =>
            requiredRouterIds.Contains(m.Id)
            && IsRouterModelLoading(m));
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
        _runtimeProfileResolver.ResolveAsync(parsed.RuntimeProfileId).GetAwaiter().GetResult();
        return new ModelRuntimeConfigDto(
            parsed.RouterModelId,
            parsed.RuntimeProfileId,
            parsed.LoadParams);
    }
}

