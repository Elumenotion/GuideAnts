using System.Collections.Concurrent;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage;

/// <summary>
/// Short-TTL per-process cache of <c>Models.ReasoningChoicesJson</c> keyed by model id, so
/// <see cref="DatabaseStorage.ResolveModelReasoningEffortAsync"/> doesn't open a DbContext on
/// every chat run. Catalog edits propagate within <see cref="Ttl"/>; a cached null means the
/// model row exists but declares no choices (or the row is absent) — both are valid to cache.
/// </summary>
public static class ReasoningChoicesCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private sealed record CacheEntry(string? Json, DateTime CachedAtUtc);

    private static readonly ConcurrentDictionary<string, CacheEntry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string modelId, DateTime nowUtc, out string? reasoningChoicesJson)
    {
        reasoningChoicesJson = null;
        if (!Entries.TryGetValue(modelId, out var entry))
        {
            return false;
        }

        if (nowUtc - entry.CachedAtUtc >= Ttl)
        {
            Entries.TryRemove(modelId, out _);
            return false;
        }

        reasoningChoicesJson = entry.Json;
        return true;
    }

    public static void Set(string modelId, string? reasoningChoicesJson, DateTime nowUtc) =>
        Entries[modelId] = new CacheEntry(reasoningChoicesJson, nowUtc);

    public static void Clear() => Entries.Clear();
}
