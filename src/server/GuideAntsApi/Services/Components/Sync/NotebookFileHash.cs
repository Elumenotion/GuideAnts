using System.Security.Cryptography;

namespace GuideAntsApi.Services.Components.Sync;

/// <summary>
/// Notebook file content hashing and the metadata gate that decides when hashing is required.
/// </summary>
/// <remarks>
/// Reconcile is metadata-first: size + last-write time decide whether bytes could have changed.
/// SHA-256 runs only for new files, metadata changes, or placeholder hashes from fast-register.
/// Do not reintroduce full-tree hashing of unchanged files.
/// </remarks>
public static class NotebookFileHash
{
    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    public static string Placeholder(long size, DateTime lastModifiedUtc) =>
        $"pending:{size:x}:{ToUtcSecondTicks(lastModifiedUtc):x}";

    public static bool IsPlaceholder(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.StartsWith("pending:", StringComparison.Ordinal);

    /// <summary>
    /// True when disk metadata matches the DB row closely enough that content hashing is unnecessary.
    /// Requires a real (non-placeholder) hash already stored.
    /// </summary>
    public static bool IsUnchanged(
        long dbSize,
        DateTime dbLastModifiedUtc,
        string? dbFileHash,
        long diskSize,
        DateTime diskLastModifiedUtc)
    {
        if (IsPlaceholder(dbFileHash))
        {
            return false;
        }

        if (dbSize != diskSize)
        {
            return false;
        }

        // Content-files often live on Docker Desktop bind mounts where sub-second mtimes are not
        // stable across reads. Whole-second UTC equality is the durable unchanged signal.
        return ToUtcSecondTicks(dbLastModifiedUtc) == ToUtcSecondTicks(diskLastModifiedUtc);
    }

    /// <summary>
    /// Normalize filesystem/DB timestamps to whole UTC seconds for durable comparison.
    /// </summary>
    public static long ToUtcSecondTicks(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond);
    }
}
