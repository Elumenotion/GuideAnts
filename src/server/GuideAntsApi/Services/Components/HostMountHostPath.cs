namespace GuideAntsApi.Services.Components;

/// <summary>
/// Host-path helpers for mount sources. The API often runs on Linux while admins
/// supply Windows or macOS absolute paths for Docker bind mounts.
/// </summary>
public static class HostMountHostPath
{
    public static bool IsAbsoluteHostPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        if (trimmed.StartsWith('/'))
        {
            return true;
        }

        if (trimmed.Length >= 3
            && char.IsAsciiLetter(trimmed[0])
            && trimmed[1] == ':'
            && (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return true;
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return Path.IsPathRooted(trimmed);
    }

    public static string GetLeafName(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return string.Empty;
        }

        var trimmed = hostPath.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var lastSeparator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        if (lastSeparator >= 0 && lastSeparator < trimmed.Length - 1)
        {
            return trimmed[(lastSeparator + 1)..];
        }

        return Path.GetFileName(trimmed) ?? trimmed;
    }
}
