using System.Text.Json.Nodes;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.IntegrationTests.TestUtils;

/// <summary>
/// In-memory stub of <see cref="ILlamaServerRuntimeClient"/> used by Phase G
/// integration tests for the settings + routing HTTP surface. Tracks per-alias
/// load state and supports synthetic latency injection so tests can exercise
/// the R-12 concurrency semantics of the load coordinator without touching a
/// real llama-server process.
/// </summary>
public sealed class StubLlamaServerRuntimeClient : ILlamaServerRuntimeClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _loadGates = new(StringComparer.Ordinal);
    private readonly List<string> _loadLog = new();
    private readonly List<string> _unloadLog = new();

    public IReadOnlyList<string> LoadLog
    {
        get { lock (_gate) { return _loadLog.ToArray(); } }
    }

    public IReadOnlyList<string> UnloadLog
    {
        get { lock (_gate) { return _unloadLog.ToArray(); } }
    }

    /// <summary>
    /// When set for an alias, <see cref="LoadModelAsync"/> will await the task
    /// before transitioning the alias to "loaded". Tests use this to hold the
    /// load in flight and observe lock / state semantics.
    /// </summary>
    public void SetLoadGate(string alias, TaskCompletionSource gate)
    {
        lock (_gate)
        {
            _loadGates[alias] = gate;
        }
    }

    public void ClearLoadGate(string alias)
    {
        lock (_gate)
        {
            _loadGates.Remove(alias);
        }
    }

    public void SeedState(string alias, string state)
    {
        lock (_gate)
        {
            _states[alias] = state;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _states.Clear();
            _loadGates.Clear();
            _loadLog.Clear();
            _unloadLog.Clear();
        }
    }

    public string GetState(string alias)
    {
        lock (_gate)
        {
            return _states.TryGetValue(alias, out var s) ? s : "unloaded";
        }
    }

    public Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        LlamaModelsResponse response;
        lock (_gate)
        {
            response = new LlamaModelsResponse
            {
                Data = _states.Select(kvp => new LlamaModelData
                {
                    Id = kvp.Key,
                    Status = new LlamaModelStatus { Value = kvp.Value },
                    State = kvp.Value
                }).ToList()
            };
        }

        return Task.FromResult(response);
    }

    public Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlamaOpenAiModelsResponse());

    public async Task LoadModelAsync(string modelPathOrPreset, JsonObject? loadParams = null, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? gate;
        lock (_gate)
        {
            _loadLog.Add(modelPathOrPreset);
            _states[modelPathOrPreset] = "loading";
            _loadGates.TryGetValue(modelPathOrPreset, out gate);
        }

        if (gate != null)
        {
            using var reg = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            await gate.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            _states[modelPathOrPreset] = "loaded";
        }
    }

    public Task UnloadModelAsync(string routerModelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _unloadLog.Add(routerModelId);
            _states[routerModelId] = "unloaded";
        }

        return Task.CompletedTask;
    }
}
