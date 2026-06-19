using System.Text.Json;

namespace ScriptExecutionAgent;

internal enum MountsRegistryLoadStatus
{
    Missing,
    Loaded,
    Malformed
}

internal sealed record MountRegistryEntry(
    string MountId,
    string LeafName,
    string LinkRelativePath,
    string ContainerSourcePath,
    bool Writable);

internal sealed class NotebookMountsRegistry
{
    public static NotebookMountsRegistry Empty { get; } = new([]);

    public IReadOnlyList<MountRegistryEntry> Mounts { get; }

    private NotebookMountsRegistry(IReadOnlyList<MountRegistryEntry> mounts)
    {
        Mounts = mounts;
    }

    public MountRegistryEntry? FindByLinkRelativePath(string linkRelativePath)
    {
        foreach (var mount in Mounts)
        {
            if (string.Equals(mount.LinkRelativePath, linkRelativePath, StringComparison.Ordinal))
            {
                return mount;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="candidatePath"/> resolves under a registered host-mount source root.
    /// Ownership prep must not run on bind-mounted host folders.
    /// </summary>
    public bool IsUnderAnyContainerSourcePath(string candidatePath)
    {
        string normalizedCandidate;
        try
        {
            normalizedCandidate = NormalizeDirectoryPath(candidatePath);
        }
        catch
        {
            return false;
        }

        foreach (var mount in Mounts)
        {
            string normalizedSource;
            try
            {
                normalizedSource = NormalizeDirectoryPath(mount.ContainerSourcePath);
            }
            catch
            {
                continue;
            }

            if (IsStrictChildOrSamePath(normalizedSource, normalizedCandidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsStrictChildOrSamePath(string root, string path)
    {
        root = NormalizeDirectoryPath(root);
        path = NormalizeDirectoryPath(path);
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static (NotebookMountsRegistry? Registry, MountsRegistryLoadStatus Status, string? Error) TryLoad(string notebookRoot)
    {
        var registryPath = Path.Combine(notebookRoot, ".guideants", "mounts.json");
        if (!File.Exists(registryPath))
        {
            return (Empty, MountsRegistryLoadStatus.Missing, null);
        }

        try
        {
            using var stream = File.OpenRead(registryPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number ||
                schemaVersionElement.GetInt32() != 1)
            {
                return (null, MountsRegistryLoadStatus.Malformed, "mounts registry schemaVersion is invalid");
            }

            if (!root.TryGetProperty("mounts", out var mountsElement) ||
                mountsElement.ValueKind != JsonValueKind.Array)
            {
                return (null, MountsRegistryLoadStatus.Malformed, "mounts registry mounts array is invalid");
            }

            var entries = new List<MountRegistryEntry>();
            foreach (var mountElement in mountsElement.EnumerateArray())
            {
                if (!TryParseMountEntry(mountElement, out var entry, out var parseError))
                {
                    return (null, MountsRegistryLoadStatus.Malformed, parseError);
                }

                entries.Add(entry);
            }

            return (new NotebookMountsRegistry(entries), MountsRegistryLoadStatus.Loaded, null);
        }
        catch (JsonException)
        {
            return (null, MountsRegistryLoadStatus.Malformed, "mounts registry JSON is invalid");
        }
        catch (Exception ex)
        {
            return (null, MountsRegistryLoadStatus.Malformed, $"mounts registry read failed: {ex.Message}");
        }
    }

    private static bool TryParseMountEntry(JsonElement mountElement, out MountRegistryEntry entry, out string error)
    {
        entry = null!;
        error = string.Empty;

        if (!TryGetRequiredString(mountElement, "mountId", out var mountId) ||
            !TryGetRequiredString(mountElement, "leafName", out var leafName) ||
            !TryGetRequiredString(mountElement, "linkRelativePath", out var linkRelativePath) ||
            !TryGetRequiredString(mountElement, "containerSourcePath", out var containerSourcePath) ||
            !mountElement.TryGetProperty("writable", out var writableElement) ||
            writableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "mounts registry entry is missing required fields";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mountId) ||
            string.IsNullOrWhiteSpace(leafName) ||
            string.IsNullOrWhiteSpace(linkRelativePath) ||
            string.IsNullOrWhiteSpace(containerSourcePath))
        {
            error = "mounts registry entry contains empty values";
            return false;
        }

        entry = new MountRegistryEntry(
            mountId,
            leafName,
            linkRelativePath,
            containerSourcePath,
            writableElement.GetBoolean());
        return true;
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
}
