using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Process-wide cache for host-mount shallow scans and one-level listings.
/// Keys: <c>shallow:{mountKey}</c> and <c>level:{mountKey}:{relativePath}</c>.
/// </summary>
public static class HostMountListingCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(DateTimeOffset ExpiresUtc, HostMountDirectoryScanner.ScanResult Result);

    public static bool TryGet(string key, out HostMountDirectoryScanner.ScanResult result)
    {
        PruneExpired(DateTimeOffset.UtcNow);
        if (Entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            result = entry.Result;
            return true;
        }

        result = null!;
        return false;
    }

    public static void Set(string key, HostMountDirectoryScanner.ScanResult result, TimeSpan ttl)
    {
        Entries[key] = new CacheEntry(DateTimeOffset.UtcNow + ttl, result);
    }

    public static string ShallowKey(string mountKey) => $"shallow:{NormalizeMountKey(mountKey)}";

    public static string LevelKey(string mountKey, string relativePath) =>
        $"level:{NormalizeMountKey(mountKey)}:{NormalizeRelative(relativePath)}";

    public static void InvalidateMount(string mountKey)
    {
        var prefixShallow = $"shallow:{NormalizeMountKey(mountKey)}";
        var prefixLevel = $"level:{NormalizeMountKey(mountKey)}:";
        foreach (var key in Entries.Keys)
        {
            if (key.Equals(prefixShallow, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefixLevel, StringComparison.OrdinalIgnoreCase))
            {
                Entries.TryRemove(key, out _);
            }
        }
    }

    public static void InvalidatePath(string mountKey, string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        Entries.TryRemove(LevelKey(mountKey, normalized), out _);

        // Parent listings and the shallow page may include this path.
        InvalidateMount(mountKey);
    }

    public static void ClearAll() => Entries.Clear();

    private static void PruneExpired(DateTimeOffset now)
    {
        foreach (var kvp in Entries)
        {
            if (kvp.Value.ExpiresUtc <= now)
            {
                Entries.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string NormalizeMountKey(string mountKey) =>
        (mountKey ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeRelative(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim('/');
}
