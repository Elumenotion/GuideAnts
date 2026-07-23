using System.Text.Json;

namespace GuideAntsApi.Services.Components.Sync;

internal sealed record NotebookMountRegistryEntry(
    Guid MountId,
    string LeafName,
    string LinkRelativePath,
    string ContainerSourcePath,
    bool Writable);

internal static class NotebookMountsRegistryLoader
{
    internal const string RegistryFolderName = ".guideants";
    internal const string RegistryFileName = "mounts.json";

    internal static IReadOnlyList<NotebookMountRegistryEntry> LoadRegisteredMounts(string notebookRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notebookRoot);

        var registryPath = Path.Combine(notebookRoot, RegistryFolderName, RegistryFileName);
        if (!File.Exists(registryPath))
        {
            return [];
        }

        using var stream = File.OpenRead(registryPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            schemaVersionElement.GetInt32() != 1 ||
            !root.TryGetProperty("mounts", out var mountsElement) ||
            mountsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("mounts registry is malformed");
        }

        var entries = new List<NotebookMountRegistryEntry>();
        foreach (var mountElement in mountsElement.EnumerateArray())
        {
            if (!mountElement.TryGetProperty("mountId", out var mountIdElement) ||
                mountIdElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(mountIdElement.GetString(), out var mountId) ||
                !TryGetRequiredString(mountElement, "leafName", out var leafName) ||
                !TryGetRequiredString(mountElement, "linkRelativePath", out var linkRelativePath) ||
                !TryGetRequiredString(mountElement, "containerSourcePath", out var containerSourcePath) ||
                !mountElement.TryGetProperty("writable", out var writableElement) ||
                writableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidOperationException("mounts registry entry is missing required fields");
            }

            entries.Add(new NotebookMountRegistryEntry(
                mountId,
                leafName,
                NormalizeRelativePath(linkRelativePath),
                containerSourcePath,
                writableElement.GetBoolean()));
        }

        return entries;
    }

    internal static HashSet<string> LoadRegisteredMountRelativePaths(string notebookRoot)
    {
        var mounts = LoadRegisteredMounts(notebookRoot);
        return mounts
            .Select(m => m.LinkRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');
}
