using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.IntegrationTests.TestUtils;

/// <summary>
/// In-memory stub for <see cref="IRouterModelsConfigService"/> so integration
/// tests can prime known router aliases without touching the on-disk
/// <c>router-models.ini</c> file. The production inventory service always
/// routes through this interface, so tests get a full end-to-end slice through
/// the inventory endpoints without filesystem coupling.
/// </summary>
public sealed class StubRouterModelsConfigService : IRouterModelsConfigService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RouterModelEntry> _entries = new(StringComparer.Ordinal);

    public void SeedEntry(string alias, string modelPath, string mmprojPath)
    {
        lock (_gate)
        {
            _entries[alias] = new RouterModelEntry(alias, modelPath, mmprojPath);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public Task<IReadOnlyList<RouterModelEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RouterModelEntry> snapshot;
        lock (_gate)
        {
            snapshot = _entries.Values.ToArray();
        }

        return Task.FromResult(snapshot);
    }

    public Task AddOrUpdateEntryAsync(string alias, string modelContainerPath, string mmprojContainerPath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries[alias] = new RouterModelEntry(alias, modelContainerPath, mmprojContainerPath);
        }

        return Task.CompletedTask;
    }
}
