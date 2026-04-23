namespace GuideAntsApi.Services;

/// <summary>
/// Backward-compatible resolver used by unit tests and legacy call sites.
/// Keeps GUID-based paths and does not perform slug/metadata lookups.
/// </summary>
public sealed class LegacyStoragePathResolver(string storageRoot) : IStoragePathResolver
{
    private readonly string _storageRoot = storageRoot;

    public string GetStorageRoot() => _storageRoot;
    public void InvalidateProject(Guid projectId) { }
    public void InvalidateNotebook(Guid notebookId) { }

    public string GetProjectRootPath(Guid projectId) => Path.Combine(_storageRoot, projectId.ToString());

    public string GetNotebookRootPath(Guid projectId, Guid notebookId) =>
        Path.Combine(_storageRoot, projectId.ToString(), "notebooks", notebookId.ToString());

    public string GetContainerNotebookRootPath(Guid projectId, Guid notebookId) =>
        $"/app/ContentFiles/{projectId}/notebooks/{notebookId}";

    public string GetContentAddressablePath(Guid projectId, string contentHash)
    {
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(_storageRoot, "projects", projectId.ToString(), "content", prefix, subdir, contentHash);
    }

    public string GetProjectMarkdownShadowPath(Guid projectId, string contentHash)
    {
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(_storageRoot, "projects", projectId.ToString(), "content", prefix, subdir, $"{contentHash}.md");
    }

    public string GetNotebookMarkdownShadowPath(Guid projectId, Guid notebookId, string contentHash)
    {
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(_storageRoot, "projects", projectId.ToString(), "notebooks", notebookId.ToString(), "markdown", prefix, subdir, $"{contentHash}.md");
    }
}
