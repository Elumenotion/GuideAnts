using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// Provenance of a last-known inventory snapshot. <see cref="None"/> means the
/// endpoint has never been observed (cold cache after an API restart — limit
/// L-2); <see cref="Live"/> means the most recent probe succeeded;
/// <see cref="LastKnown"/> means a probe failed and the previously good snapshot
/// is retained (a malformed or failed read never clobbers a good one, P6).
/// </summary>
public enum InventorySource
{
    None = 0,
    Live = 1,
    LastKnown = 2,
}

/// <summary>
/// Per-service last-known model/bundle inventory (Plane C). Written only by the
/// background probe loop and by successful download/load operations; read
/// synchronously by configuration surfaces.
///
/// <para>
/// This is observability data, never an instruction (D3/D4): a mirror snapshot
/// may never become a selection authority, and no code path may write a
/// <c>ServiceModes</c> selection from a mirror or <c>/ready</c> read (T-A2).
/// Presence shown here is last-known presence, not an "is it there now" answer
/// (limit L-1) — settling presence requires the engine to answer (Plane C live),
/// which is never resolved with a disk scan.
/// </para>
/// </summary>
public interface ILocalInventoryMirror
{
    /// <summary>
    /// Synchronous read of the last-known inventory for a service. Returns false
    /// (and an empty set) when nothing has ever been observed. No I/O.
    /// </summary>
    bool TryGet(string serviceId, out IReadOnlyList<NotebookToolbarLocalModelOptionDto> models, out InventorySource source, out DateTime? observedUtc);

    /// <summary>
    /// Records a successful (live) inventory read. Replaces any prior snapshot.
    /// </summary>
    void SetLive(string serviceId, IReadOnlyList<NotebookToolbarLocalModelOptionDto> models, DateTime observedUtc);

    /// <summary>
    /// Marks the most recent read as failed. A good prior snapshot is retained
    /// and re-stamped <see cref="InventorySource.LastKnown"/> with its original
    /// <see cref="DateTime"/> — never clobbered by a malformed or failed read.
    /// </summary>
    void MarkFailed(string serviceId, DateTime observedUtc);
}
