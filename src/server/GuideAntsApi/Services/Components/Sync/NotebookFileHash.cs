using System.Security.Cryptography;

namespace GuideAntsApi.Services.Components.Sync;

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
        $"pending:{size:x}:{lastModifiedUtc.Ticks:x}";

    public static bool IsPlaceholder(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.StartsWith("pending:", StringComparison.Ordinal);
}
