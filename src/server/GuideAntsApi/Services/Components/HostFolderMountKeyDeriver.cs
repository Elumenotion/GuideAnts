namespace GuideAntsApi.Services.Components;

public static class HostFolderMountKeyDeriver
{
    public const string ContainerMountRoot = "/app/HostMounts";

    /// <summary>
    /// Stable filesystem-safe mount key from the mount id (plan §8: {mountId:N}).
    /// </summary>
    public static string DeriveMountKey(Guid mountId) =>
        mountId.ToString("N").ToLowerInvariant();

    public static string DeriveContainerSourcePath(string mountKey) =>
        $"{ContainerMountRoot}/{mountKey}";

    /// <summary>
    /// Default leaf name from the host folder path (plan §8).
    /// </summary>
    public static string DeriveDefaultLeafName(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return string.Empty;
        }

        return HostMountHostPath.GetLeafName(hostPath);
    }

    public static string DeriveDisplayName(string hostPath) => DeriveDefaultLeafName(hostPath);
}
