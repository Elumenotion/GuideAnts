using System.Collections.Concurrent;
using System.Text.Json;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services;

public interface IStoragePathResolver
{
    string GetStorageRoot();
    string GetProjectRootPath(Guid projectId);
    string GetNotebookRootPath(Guid projectId, Guid notebookId);
    string GetContainerNotebookRootPath(Guid projectId, Guid notebookId);
    string GetContentAddressablePath(Guid projectId, string contentHash);
    string GetProjectMarkdownShadowPath(Guid projectId, string contentHash);
    string GetNotebookMarkdownShadowPath(Guid projectId, Guid notebookId, string contentHash);
    void InvalidateProject(Guid projectId);
    void InvalidateNotebook(Guid notebookId);
}

public sealed class StoragePathResolver(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StoragePathResolver> logger) : IStoragePathResolver
{
    private const string NotebookMetadataFolder = ".guideants";
    private const string NotebookMetadataFile = "notebook.json";

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<StoragePathResolver> _logger = logger;
    private readonly string _storageRoot = configuration["FileStorage:Path"]
        ?? throw new InvalidOperationException("FileStorage:Path is not configured");

    private readonly ConcurrentDictionary<Guid, string> _projectSlugCache = new();
    private readonly ConcurrentDictionary<Guid, (Guid ProjectId, string Slug)> _notebookSlugCache = new();

    public string GetStorageRoot() => _storageRoot;

    public void InvalidateProject(Guid projectId) => _projectSlugCache.TryRemove(projectId, out _);

    public void InvalidateNotebook(Guid notebookId) => _notebookSlugCache.TryRemove(notebookId, out _);

    public string GetProjectRootPath(Guid projectId)
    {
        var projectSlug = GetProjectSlug(projectId);
        var path = Path.Combine(_storageRoot, projectSlug);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetNotebookRootPath(Guid projectId, Guid notebookId)
    {
        var projectRoot = GetProjectRootPath(projectId);
        var notebookSlug = GetNotebookSlug(projectId, notebookId);
        var expectedPath = Path.Combine(projectRoot, notebookSlug);

        if (Directory.Exists(expectedPath))
        {
            EnsureNotebookAssociationMetadata(expectedPath, projectId, notebookId);
            return expectedPath;
        }

        var discoveredPath = TryFindNotebookFolderByMetadata(projectRoot, projectId, notebookId);
        if (!string.IsNullOrWhiteSpace(discoveredPath))
        {
            _logger.LogInformation("Resolved notebook {NotebookId} to externally renamed folder {FolderPath}", notebookId, discoveredPath);
            return discoveredPath;
        }

        Directory.CreateDirectory(expectedPath);
        EnsureNotebookAssociationMetadata(expectedPath, projectId, notebookId);
        return expectedPath;
    }

    public string GetContainerNotebookRootPath(Guid projectId, Guid notebookId)
    {
        var projectSlug = GetProjectSlug(projectId);
        var notebookSlug = GetNotebookSlug(projectId, notebookId);
        return $"/app/ContentFiles/{projectSlug}/{notebookSlug}";
    }

    public string GetContentAddressablePath(Guid projectId, string contentHash)
    {
        var projectSlug = GetProjectSlug(projectId);
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(_storageRoot, "projects", projectSlug, "content", prefix, subdir, contentHash);
    }

    public string GetProjectMarkdownShadowPath(Guid projectId, string contentHash)
    {
        var projectSlug = GetProjectSlug(projectId);
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(_storageRoot, "projects", projectSlug, "content", prefix, subdir, $"{contentHash}.md");
    }

    public string GetNotebookMarkdownShadowPath(Guid projectId, Guid notebookId, string contentHash)
    {
        var projectSlug = GetProjectSlug(projectId);
        var notebookSlug = GetNotebookSlug(projectId, notebookId);
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(_storageRoot, "projects", projectSlug, notebookSlug, "markdown", prefix, subdir, $"{contentHash}.md");
    }

    private string GetProjectSlug(Guid projectId)
    {
        if (_projectSlugCache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var project = db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Slug, p.Title })
            .FirstOrDefault();

        var slug = project?.Slug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = SlugGenerator.Generate(project?.Title ?? projectId.ToString());
        }

        _projectSlugCache[projectId] = slug;
        return slug;
    }

    private string GetNotebookSlug(Guid projectId, Guid notebookId)
    {
        if (_notebookSlugCache.TryGetValue(notebookId, out var cached))
        {
            if (cached.ProjectId == projectId)
            {
                return cached.Slug;
            }

            _notebookSlugCache.TryRemove(notebookId, out _);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notebook = db.Notebooks
            .AsNoTracking()
            .Where(n => n.Id == notebookId)
            .Select(n => new { n.ProjectId, n.Slug, n.Title })
            .FirstOrDefault();

        var slug = notebook?.Slug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = SlugGenerator.Generate(notebook?.Title ?? notebookId.ToString());
        }

        var resolvedProjectId = notebook?.ProjectId ?? projectId;
        _notebookSlugCache[notebookId] = (resolvedProjectId, slug);
        return slug;
    }

    private string? TryFindNotebookFolderByMetadata(string projectRoot, Guid projectId, Guid notebookId)
    {
        if (!Directory.Exists(projectRoot))
        {
            return null;
        }

        foreach (var folder in Directory.EnumerateDirectories(projectRoot))
        {
            var metadataPath = Path.Combine(folder, NotebookMetadataFolder, NotebookMetadataFile);
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            try
            {
                var metadata = JsonSerializer.Deserialize<NotebookAssociationMetadata>(File.ReadAllText(metadataPath));
                if (metadata != null && metadata.ProjectId == projectId && metadata.NotebookId == notebookId)
                {
                    return folder;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed reading notebook metadata {MetadataPath}", metadataPath);
            }
        }

        return null;
    }

    private void EnsureNotebookAssociationMetadata(string notebookRoot, Guid projectId, Guid notebookId)
    {
        var systemFolder = Path.Combine(notebookRoot, NotebookMetadataFolder);
        Directory.CreateDirectory(systemFolder);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var attrs = File.GetAttributes(systemFolder);
                if (!attrs.HasFlag(FileAttributes.Hidden))
                {
                    File.SetAttributes(systemFolder, attrs | FileAttributes.Hidden);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to set hidden attribute for {SystemFolder}", systemFolder);
        }

        var metadataPath = Path.Combine(systemFolder, NotebookMetadataFile);
        var metadata = new NotebookAssociationMetadata
        {
            SchemaVersion = 1,
            ProjectId = projectId,
            NotebookId = notebookId
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class NotebookAssociationMetadata
    {
        public int SchemaVersion { get; set; } = 1;
        public Guid ProjectId { get; set; }
        public Guid NotebookId { get; set; }
    }
}
