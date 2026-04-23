using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class IndexDirectTextFileHandler : JobHandlerBase<IndexDirectTextFileJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHybridIndexer _indexer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IndexDirectTextFileHandler> _log;

    private static readonly HashSet<string> DirectIndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".xml", ".puml", ".yaml", ".yml", ".csv", ".sql", ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".h"
    };

    public IndexDirectTextFileHandler(
        ILogger<IndexDirectTextFileHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IConfiguration configuration,
        IHybridIndexer indexer) : base(logger)
    {
        _dbFactory = dbFactory;
        _indexer = indexer;
        _configuration = configuration;
        _log = logger;
    }

    public override string JobType => nameof(IndexDirectTextFileJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(IndexDirectTextFileJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        if (payload.IsContentFile)
        {
            var version = await context.ContentFileVersions
                .Include(v => v.ContentFile)
                .FirstOrDefaultAsync(v => v.Id == payload.FileId, cancellationToken);
                
            if (version == null || string.IsNullOrEmpty(version.StoragePath))
            {
                _log.LogWarning("ContentFileVersion {Id} not found or file missing", payload.FileId);
                return false;
            }

            var storageRoot = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
            if (!StoragePathCompatibility.TryResolveExistingFilePath(version.StoragePath, storageRoot, out var filePath))
            {
                _log.LogWarning("ContentFileVersion {Id} not found or file missing", payload.FileId);
                return false;
            }

            if (!IsDirectIndexable(version.FileName))
            {
                _log.LogDebug("File {FileName} is not directly indexable", version.FileName);
                return true;
            }

            try
            {
                await _indexer.IndexContentFileAsync(payload.FileId, version.ContentFile.ProjectId, filePath, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Direct indexing failed for ContentFileVersion {Id}", payload.FileId);
                return false;
            }
        }
        else
        {
            var notebookFile = await context.NotebookFiles
                .Include(f => f.Notebook)
                .FirstOrDefaultAsync(f => f.Id == payload.FileId, cancellationToken);
                
            if (notebookFile == null)
            {
                _log.LogWarning("NotebookFile {Id} not found", payload.FileId);
                return false;
            }

            var storageRoot = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
            var normalizedRelativePath = notebookFile.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
            var projectSlug = await context.Projects
                .Where(p => p.Id == notebookFile.Notebook.ProjectId)
                .Select(p => p.Slug)
                .FirstOrDefaultAsync(cancellationToken) ?? notebookFile.Notebook.ProjectId.ToString();
            var notebookSlug = string.IsNullOrWhiteSpace(notebookFile.Notebook.Slug)
                ? notebookFile.NotebookId.ToString()
                : notebookFile.Notebook.Slug;
            var filePath = Path.Combine(storageRoot, projectSlug, notebookSlug, normalizedRelativePath);
            if (!File.Exists(filePath))
            {
                filePath = Path.Combine(storageRoot, notebookFile.Notebook.ProjectId.ToString(), "notebooks", notebookFile.NotebookId.ToString(), normalizedRelativePath);
            }
            
            if (!File.Exists(filePath))
            {
                _log.LogWarning("NotebookFile physical file not found: {Path}. RelativePath was: {RelativePath}. Storage root: {StorageRoot}. Working directory: {WorkingDir}", 
                    filePath, notebookFile.RelativePath, storageRoot, Directory.GetCurrentDirectory());
                
                // List what files actually exist in the directory
                var parentDir = Path.GetDirectoryName(filePath);
                if (Directory.Exists(parentDir))
                {
                    var existingFiles = Directory.GetFiles(parentDir, "*", SearchOption.TopDirectoryOnly);
                    _log.LogInformation("Files in directory {ParentDir}: {Files}", parentDir, string.Join(", ", existingFiles.Select(Path.GetFileName)));
                }
                else
                {
                    _log.LogWarning("Parent directory does not exist: {ParentDir}", parentDir);
                }
                
                return false;
            }

            if (!IsDirectIndexable(notebookFile.RelativePath))
            {
                _log.LogDebug("File {FileName} is not directly indexable", notebookFile.RelativePath);
                return true;
            }

            try
            {
                await _indexer.IndexNotebookFileAsync(payload.FileId, notebookFile.NotebookId, notebookFile.Notebook.ProjectId, filePath, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Direct indexing failed for NotebookFile {Id}", payload.FileId);
                return false;
            }
        }
    }

    private static bool IsDirectIndexable(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return DirectIndexableExtensions.Contains(extension);
    }
}
