using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.Components;

public sealed class NotebookMountsRegistry
{
    public const int CurrentSchemaVersion = 1;

    public const string RegistryFolderName = ".guideants";

    public const string RegistryFileName = "mounts.json";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("mounts")]
    public List<NotebookMountsRegistryEntry> Mounts { get; set; } = [];
}

public sealed class NotebookMountsRegistryEntry
{
    [JsonPropertyName("mountId")]
    public Guid MountId { get; set; }

    [JsonPropertyName("leafName")]
    public string LeafName { get; set; } = string.Empty;

    [JsonPropertyName("linkRelativePath")]
    public string LinkRelativePath { get; set; } = string.Empty;

    [JsonPropertyName("containerSourcePath")]
    public string ContainerSourcePath { get; set; } = string.Empty;

    [JsonPropertyName("writable")]
    public bool Writable { get; set; } = true;
}

public sealed class NotebookMountsRegistryWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void WriteRegistry(string notebookRoot, NotebookMountsRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notebookRoot);
        ArgumentNullException.ThrowIfNull(registry);

        var guideantsFolder = Path.Combine(notebookRoot, NotebookMountsRegistry.RegistryFolderName);
        Directory.CreateDirectory(guideantsFolder);

        var registryPath = Path.Combine(guideantsFolder, NotebookMountsRegistry.RegistryFileName);
        var serialized = JsonSerializer.Serialize(registry, SerializerOptions);
        WriteFileAtomically(registryPath, serialized);
    }

    private static void WriteFileAtomically(string destinationPath, string content)
    {
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(tempPath, destinationPath, overwrite: true);
                    File.Delete(tempPath);
                }
                catch (UnauthorizedAccessException)
                {
                    File.Copy(tempPath, destinationPath, overwrite: true);
                    File.Delete(tempPath);
                }
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }
}
