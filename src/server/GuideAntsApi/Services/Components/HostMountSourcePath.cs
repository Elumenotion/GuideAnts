namespace GuideAntsApi.Services.Components;

internal static class HostMountSourcePath
{
    public static bool TryResolvePhysicalSourcePath(
        string hostMountsRoot,
        string containerSourcePath,
        out string physicalSourcePath)
    {
        physicalSourcePath = string.Empty;

        if (string.IsNullOrWhiteSpace(hostMountsRoot) || string.IsNullOrWhiteSpace(containerSourcePath))
        {
            return false;
        }

        var normalizedRoot = NormalizeRoot(hostMountsRoot);
        const string normalizedContainerRoot = HostFolderMountKeyDeriver.ContainerMountRoot;

        if (!TryNormalizeContainerPath(containerSourcePath, out var normalizedContainerPath))
        {
            return false;
        }

        if (!normalizedContainerPath.StartsWith(normalizedContainerRoot + '/', StringComparison.Ordinal)
            && !string.Equals(normalizedContainerPath, normalizedContainerRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var relativeMountKey = normalizedContainerPath.Length == normalizedContainerRoot.Length
            ? string.Empty
            : normalizedContainerPath[(normalizedContainerRoot.Length + 1)..];

        if (string.IsNullOrWhiteSpace(relativeMountKey)
            || relativeMountKey.Contains('/', StringComparison.Ordinal)
            || relativeMountKey.Contains('\\', StringComparison.Ordinal)
            || relativeMountKey.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativeMountKey))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!IsStrictChildOrSamePath(normalizedRoot, combined))
        {
            return false;
        }

        physicalSourcePath = combined;
        return true;
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool TryNormalizeContainerPath(string containerSourcePath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(containerSourcePath) || containerSourcePath.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        normalizedPath = containerSourcePath.Replace('\\', '/').TrimEnd('/');
        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsStrictChildOrSamePath(string root, string path)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
