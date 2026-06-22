using System.Text.Json;

namespace ScriptExecutionAgent.Tests.Infrastructure;

public sealed class NotebookStorageFixture : IDisposable
{
    public Guid ProjectId { get; } = Guid.NewGuid();
    public Guid NotebookId { get; } = Guid.NewGuid();
    public Guid GuideId { get; } = Guid.NewGuid();
    public string StorageRoot { get; }
    public string NotebookRoot { get; }
    public string WorkingDirectory { get; }

    public NotebookStorageFixture(string storageRoot)
    {
        StorageRoot = storageRoot;
        var projectSlug = $"project-{ProjectId:N}";
        var notebookSlug = $"notebook-{NotebookId:N}";
        NotebookRoot = Path.Combine(StorageRoot, projectSlug, notebookSlug);
        WorkingDirectory = Path.Combine(NotebookRoot, "Output");
        Directory.CreateDirectory(WorkingDirectory);

        var metadataDir = Path.Combine(NotebookRoot, ".guideants");
        Directory.CreateDirectory(metadataDir);
        var metadata = new
        {
            ProjectId = ProjectId.ToString(),
            NotebookId = NotebookId.ToString()
        };
        File.WriteAllText(
            Path.Combine(metadataDir, "notebook.json"),
            JsonSerializer.Serialize(metadata));
    }

    public void CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(NotebookRoot, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
    }
}
