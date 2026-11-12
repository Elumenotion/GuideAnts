using GuideAntsApi.Models;
using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Reachability;

public sealed class LocalInventoryMirror : ILocalInventoryMirror
{
    internal sealed record Entry(
        IReadOnlyList<NotebookToolbarLocalModelOptionDto> Models,
        InventorySource Source,
        DateTime? ObservedUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string serviceId, out IReadOnlyList<NotebookToolbarLocalModelOptionDto> models, out InventorySource source, out DateTime? observedUtc)
    {
        if (_entries.TryGetValue(serviceId, out var entry))
        {
            models = entry.Models;
            source = entry.Source;
            observedUtc = entry.ObservedUtc;
            return true;
        }

        models = Array.Empty<NotebookToolbarLocalModelOptionDto>();
        source = InventorySource.None;
        observedUtc = null;
        return false;
    }

    public void SetLive(string serviceId, IReadOnlyList<NotebookToolbarLocalModelOptionDto> models, DateTime observedUtc)
    {
        _entries[serviceId] = new Entry(models, InventorySource.Live, observedUtc);
    }

    public void MarkFailed(string serviceId, DateTime observedUtc)
    {
        // A failed probe keeps the prior snapshot (with its original ObservedUtc,
        // so the surface can say "last known · observed HH:MM") and re-stamps the
        // source LastKnown. With no prior snapshot there is nothing to retain.
        if (_entries.TryGetValue(serviceId, out var existing) && existing.Source != InventorySource.None)
        {
            _entries[serviceId] = existing with { Source = InventorySource.LastKnown };
        }
    }

    /// <summary>
    /// Full snapshot for <c>GET /api/settings/reachability</c> and diagnostics.
    /// </summary>
    internal IReadOnlyCollection<KeyValuePair<string, Entry>> Snapshot() => _entries.ToList();
}
