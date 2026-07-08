using AntRunner.ToolCalling;
using GuideAntsApi.Services;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Writes wire and script artifacts into the notebook run output directory without database sync.
/// </summary>
public static class NotebookRunOutputWriter
{
    /// <summary>UTC short datetime stamp used in wire artifact filenames.</summary>
    public const string WireFilenameTimestampFormat = "yyyyMMdd-HHmmss";

    public static string ResolveStorageRoot(IConfiguration configuration)
    {
        var storageRoot = Environment.GetEnvironmentVariable("FileStorage__Path")
            ?? Environment.GetEnvironmentVariable("FILESTORAGE__PATH");
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            storageRoot = configuration["FileStorage:Path"];
        }

        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new InvalidOperationException("FileStorage:Path is not configured");
        }

        return storageRoot;
    }

    public static string GetOutputDirectory(InvocationContext context, string storageRoot)
    {
        if (context.ProjectId == Guid.Empty || context.NotebookId == Guid.Empty)
        {
            throw new InvalidOperationException("Project ID and Notebook ID are required.");
        }

        var notebookDirectory = NotebookPathHelper.GetLocalWorkingDirectory(context, storageRoot);
        Directory.CreateDirectory(notebookDirectory);
        return notebookDirectory;
    }

    public static string CreateWireFilename(InvocationContext context, string storageRoot, string extension)
    {
        var outputDirectory = GetOutputDirectory(context, storageRoot);
        return ReserveUniqueWireFilename(outputDirectory, extension, DateTime.UtcNow);
    }

    public static string ReserveUniqueWireFilename(string outputDirectory, string extension, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        }

        var normalizedExtension = extension.Trim().TrimStart('.');
        var stamp = timestampUtc.ToString(WireFilenameTimestampFormat);
        var baseName = $"wire-{stamp}";

        var candidate = $"{baseName}.{normalizedExtension}";
        if (!File.Exists(Path.Combine(outputDirectory, candidate)))
        {
            return candidate;
        }

        for (var sequence = 1; sequence < int.MaxValue; sequence++)
        {
            candidate = $"{baseName}({sequence}).{normalizedExtension}";
            if (!File.Exists(Path.Combine(outputDirectory, candidate)))
            {
                return candidate;
            }
        }

        throw new IOException($"Unable to reserve a unique wire filename in '{outputDirectory}'.");
    }

    public static string BuildOutputFilePath(InvocationContext context, string storageRoot, string filename)
    {
        var sanitizedFilename = Path.GetFileName(filename);
        var notebookDirectory = GetOutputDirectory(context, storageRoot);
        return Path.Combine(notebookDirectory, sanitizedFilename);
    }
}
