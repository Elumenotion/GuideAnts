using Microsoft.EntityFrameworkCore;
using CliWrap;
using System.Security.Cryptography;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Endpoints;

namespace GuideAntsApi.Services.Components;

public interface INotebookService : IProjectComponentService<NotebookDto>
{
    Task<CreateNotebookFromFileResultDto> CreateNotebookFromFileAsync(
        Guid projectId, 
        CreateNotebookFromFileDto dto);
    
    Task<bool> SetHomePageFileAsync(Guid projectId, Guid notebookId, Guid fileId);
    Task<bool> ClearHomePageAsync(Guid projectId, Guid notebookId);
}

public class NotebookService : INotebookService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContentFileService _contentFileService;
    private readonly INotebookFileService _notebookFileService;
    private readonly string _storagePath;
    private readonly IStoragePathResolver _pathResolver;
    private readonly ILogger<NotebookService> _logger;

    public NotebookService(
        IServiceScopeFactory scopeFactory,
        IContentFileService contentFileService,
        INotebookFileService notebookFileService,
        IStoragePathResolver pathResolver,
        IConfiguration configuration,
        ILogger<NotebookService> logger)
    {
        _scopeFactory = scopeFactory;
        _contentFileService = contentFileService;
        _notebookFileService = notebookFileService;
        _pathResolver = pathResolver;
        _storagePath = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        _logger = logger;
    }

    // Backward-compatible overload used by tests.
    public NotebookService(
        IServiceScopeFactory scopeFactory,
        IContentFileService contentFileService,
        INotebookFileService notebookFileService,
        IConfiguration configuration,
        ILogger<NotebookService> logger)
        : this(
            scopeFactory,
            contentFileService,
            notebookFileService,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")),
            configuration,
            logger)
    { }

    /// <summary>
    /// Creates a new scope and returns the DbContext. Use with 'using' statement.
    /// </summary>
    private IServiceScope CreateDbScope() => _scopeFactory.CreateScope();

    /// <summary>
    /// Gets the DbContext from a scope. Use with CreateDbScope().
    /// </summary>
    private static ApplicationDbContext GetDbContext(IServiceScope scope) => 
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    private static NotebookDto ToNotebookDto(Notebook notebook)
    {
        // Use GuideId if set, otherwise use NotebookTemplateId (legacy)
        var guideId = notebook.GuideId ?? notebook.NotebookTemplateId ?? throw new InvalidOperationException("Notebook must have either GuideId or NotebookTemplateId");
        return new NotebookDto(notebook.Id, notebook.Title, notebook.Description, guideId, notebook.HomePageFileId, notebook.Created);
    }

    public async Task<NotebookDto> CreateAsync(Guid projectId, object createDto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        if (createDto is not CreateNotebookDto cnd)
        {
            throw new ArgumentException($"Expected CreateNotebookDto but received {createDto?.GetType().Name ?? "null"}", nameof(createDto));
        }

        var title = cnd.Title;
        var description = cnd.Description;
        var requestedGuideId = cnd.GuideId;

        // GuideId is required - no fallback logic
        if (!requestedGuideId.HasValue)
        {
            throw new ArgumentException("GuideId is required to create a notebook", nameof(createDto));
        }

        // Get owner context
        var project = await context.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync();
        
        if (project == null) throw new ArgumentException("Project not found");

        var catalogFilter = scope.ServiceProvider.GetRequiredService<ISystemGuideCatalogFilter>();
        var hiddenGuideIds = await catalogFilter.GetHiddenGuideIdsForCatalogAsync(projectId);
        if (hiddenGuideIds.Contains(requestedGuideId.Value))
        {
            throw new ArgumentException(
                $"Guide with ID {requestedGuideId.Value} is not available for this project",
                nameof(createDto));
        }

        // Verify that requestedGuideId is a valid guide ID (owner-scoped or global)
        var guide = await context.Assistants
            .AsNoTracking()
            .Where(a => a.Id == requestedGuideId.Value 
                && a.Kind == AssistantKind.Guide 
                && a.IsActive)
            .FirstOrDefaultAsync();
        
        if (guide == null)
        {
            throw new ArgumentException($"Guide with ID {requestedGuideId.Value} not found or not active", nameof(createDto));
        }
        
        var guideId = guide.Id;

        var notebook = new Notebook
        {
            ProjectId = projectId,
            Title = title,
            Slug = await GenerateUniqueNotebookSlugAsync(context, projectId, title),
            Description = description,
            GuideId = guideId
        };

        context.Notebooks.Add(notebook);
        // Note: conversation will be created via explicit endpoint when the user initiates chat

        await context.SaveChangesAsync();

        try
        {
            using var mountScope = CreateDbScope();
            var mountService = mountScope.ServiceProvider.GetRequiredService<IHostFolderMountService>();
            await mountService.ApplyProjectScopedMappingsToNewNotebookAsync(projectId, notebook.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply project-scoped host folder mounts to notebook {NotebookId}", notebook.Id);
        }

        // Copy CodeInterpreter files from guide and its crew into notebook Resourcess and create Output/ symlinks
        try
        {
            await CopyGuideFilesToNotebookAsync(context, guideId, projectId, notebook.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy guide files to notebook {NotebookId}", notebook.Id);
            // Non-fatal: notebook creation succeeds even if file copy fails
        }

        return ToNotebookDto(notebook);
    }

    public async Task<NotebookDto?> UpdateAsync(Guid projectId, Guid componentId, object updateDto)
    {

if (updateDto is not UpdateNotebookDto und)
        {
            throw new ArgumentException($"Expected UpdateNotebookDto but received {updateDto?.GetType().Name ?? "null"}", nameof(updateDto));
        }

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks
            .FirstOrDefaultAsync(n => n.Id == componentId && n.ProjectId == projectId);

        if (notebook == null)
        {
            throw new KeyNotFoundException($"Notebook {componentId} not found in project {projectId}");
        }

        notebook.Title = und.Title;
        var nextSlug = await GenerateUniqueNotebookSlugAsync(context, projectId, und.Title, notebook.Id);
        if (!string.Equals(notebook.Slug, nextSlug, StringComparison.Ordinal))
        {
            var oldRoot = GetNotebookRootPath(projectId, notebook.Id);
            notebook.Slug = nextSlug;
            _pathResolver.InvalidateNotebook(notebook.Id);
            var newRoot = GetNotebookRootPath(projectId, notebook.Id);

            if (Directory.Exists(oldRoot) && !string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(newRoot))
                {
                    throw new InvalidOperationException($"Cannot rename notebook folder to '{newRoot}' because it already exists.");
                }

                Directory.Move(oldRoot, newRoot);
            }
        }
        notebook.Description = und.Description;
        await context.SaveChangesAsync();

        return ToNotebookDto(notebook);
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid componentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks
            .FirstOrDefaultAsync(n => n.Id == componentId && n.ProjectId == projectId);

        if (notebook == null)
        {
            throw new KeyNotFoundException($"Notebook {componentId} not found in project {projectId}");
        }

        // Check if notebook is attached to an active published guide
        var publishedGuides = await context.PublishedGuides
            .Where(pg => pg.NotebookId == componentId)
            .ToListAsync();
        
        if (publishedGuides.Any(pg => pg.Active))
        {
            throw new InvalidOperationException("Cannot delete a notebook that is attached to an active published guide. Deactivate the published guide first.");
        }

        // Remove any inactive published guide references to allow notebook deletion
        if (publishedGuides.Count > 0)
        {
            context.PublishedGuides.RemoveRange(publishedGuides);
        }

        // Best-effort: delete physical notebook root including Output symlinks and Resources files
        try
        {
            var root = GetNotebookRootPath(projectId, notebook.Id);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete notebook root folder for {NotebookId}", notebook.Id);
        }

        context.Notebooks.Remove(notebook);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<NotebookDto?> GetAsync(Guid projectId, Guid componentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks
            .FirstOrDefaultAsync(n => n.Id == componentId && n.ProjectId == projectId);

        return notebook == null ? null : ToNotebookDto(notebook);
    }

    public async Task<IEnumerable<NotebookDto>> GetAllForProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebooks = await context.Notebooks
            .Where(n => n.ProjectId == projectId)
            .ToListAsync();

        return notebooks.Select(ToNotebookDto);
    }

    public async Task<CreateNotebookFromFileResultDto> CreateNotebookFromFileAsync(
        Guid projectId, 
        CreateNotebookFromFileDto dto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        // Use a database transaction to ensure atomic operation
        using var transaction = await context.Database.BeginTransactionAsync();
        
        try
        {
            // Step 1: Create the notebook
            Guid? guideId = null;
            if (!string.IsNullOrEmpty(dto.GuideId))
            {
                if (!Guid.TryParse(dto.GuideId, out var parsedId))
                {
                    throw new ArgumentException($"Invalid GuideId format: {dto.GuideId}", nameof(dto));
                }
                guideId = parsedId;
            }
            
            var createNotebookDto = new CreateNotebookDto
            {
                Title = dto.Title,
                Description = null,
                GuideId = guideId
            };
            var notebookDto = await CreateAsync(projectId, createNotebookDto);
            
            // Step 2: Copy the file to the new notebook
            var copiedFile = await _notebookFileService.CopyFromProjectAsync(
                projectId, 
                notebookDto.Id, 
                dto.ContentFileId, 
                dto.VersionNumber, 
                null // Let it use the original filename
            );
            
            if (copiedFile == null)
            {
                throw new InvalidOperationException("Failed to copy file to notebook");
            }
            
            // Commit the transaction if both operations succeeded
            await transaction.CommitAsync();
            
            return new CreateNotebookFromFileResultDto(
                notebookDto.Id,
                notebookDto.Title,
                copiedFile.Id,
                Path.GetFileName(copiedFile.RelativePath)
            );
        }
        catch
        {
            // Rollback the transaction if anything failed
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> SetHomePageFileAsync(Guid projectId, Guid notebookId, Guid fileId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks
            .FirstOrDefaultAsync(n => n.Id == notebookId && n.ProjectId == projectId);
        
        if (notebook == null)
        {
            throw new KeyNotFoundException($"Notebook {notebookId} not found in project {projectId}");
        }

        notebook.HomePageFileId = fileId;
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ClearHomePageAsync(Guid projectId, Guid notebookId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var notebook = await context.Notebooks
            .FirstOrDefaultAsync(n => n.Id == notebookId && n.ProjectId == projectId);
        
        if (notebook == null)
        {
            throw new KeyNotFoundException($"Notebook {notebookId} not found in project {projectId}");
        }

        notebook.HomePageFileId = null;
        await context.SaveChangesAsync();
        return true;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private string GetNotebookRootPath(Guid projectId, Guid notebookId)
    {
        return _pathResolver.GetNotebookRootPath(projectId, notebookId);
    }

    /// <summary>
    /// Copies CodeInterpreter files from the guide and its crew into the notebook's Resources/ directory
    /// and creates symlinks in the Output/ directory. Also creates NotebookFile records with correct metadata.
    /// </summary>
    private async Task CopyGuideFilesToNotebookAsync(
        ApplicationDbContext context,
        Guid guideId,
        Guid projectId,
        Guid notebookId)
    {
        // Gather guide's CodeInterpreter files
        var guideFiles = await context.AssistantFiles
            .Where(f => f.AssistantId == guideId && f.FolderKind == "CodeInterpreter")
            .ToListAsync();

        // Gather crew members and their files
        var crewMembers = await context.GuideMembers
            .Include(gm => gm.Assistant)
            .Where(gm => gm.GuideId == guideId)
            .ToListAsync();

        var crewAssistantIds = crewMembers.Select(cm => cm.AssistantId).Distinct().ToList();
        var crewFiles = new List<(AssistantFile File, string CrewName)>();
        if (crewAssistantIds.Count > 0)
        {
            var crewAssistantIdToName = crewMembers
                .GroupBy(cm => cm.AssistantId)
                .ToDictionary(g => g.Key, g => g.First().Assistant.Name);

            var cf = await context.AssistantFiles
                .Where(f => crewAssistantIds.Contains(f.AssistantId) && f.FolderKind == "CodeInterpreter")
                .ToListAsync();

            crewFiles = cf.Select(f => (f, crewAssistantIdToName.TryGetValue(f.AssistantId, out var n) ? n : "unknown"))
                          .ToList();
        }

        if (!guideFiles.Any() && !crewFiles.Any())
        {
            _logger.LogInformation("No CodeInterpreter files to copy for guide {GuideId}", guideId);
            return;
        }

        var notebookRoot = GetNotebookRootPath(projectId, notebookId);
        var outputDir = Path.Combine(notebookRoot, "Output");
        Directory.CreateDirectory(outputDir);

        // Track final filenames to avoid Output/ filename conflicts
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Accumulate records to add
        var notebookFilesToAdd = new List<NotebookFile>();

        // Helper local function to persist a file to disk and stage DB record
        void WriteFileAndStage(string relativeResourcePath, byte[] content)
        {
            var resourcePhysicalPath = Path.Combine(notebookRoot, relativeResourcePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            var dir = Path.GetDirectoryName(resourcePhysicalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(resourcePhysicalPath, content);

            var fileInfo = new FileInfo(resourcePhysicalPath);
            var nf = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = relativeResourcePath.Replace("\\", "/"),
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                FileHash = ComputeSha256(resourcePhysicalPath),
                OriginContentFileVersionId = null,
                Created = DateTime.UtcNow
            };
            nf.GenerateDocumentId(notebookId);
            notebookFilesToAdd.Add(nf);
        }

        // Copy guide files into resource/
        foreach (var f in guideFiles)
        {
            if (f.ContentBytes == null || f.ContentBytes.Length == 0) continue;

            var baseName = Path.GetFileName(string.IsNullOrEmpty(f.RelativePath) ? Guid.NewGuid().ToString() : f.RelativePath);
            var finalName = baseName;
            if (usedFileNames.Contains(finalName))
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(baseName);
                var ext = Path.GetExtension(baseName);
                var counter = 2;
                while (usedFileNames.Contains($"{nameNoExt}_{counter}{ext}")) counter++;
                finalName = $"{nameNoExt}_{counter}{ext}";
                _logger.LogWarning("Filename conflict detected. Renamed {Original} to {New}", baseName, finalName);
            }
            usedFileNames.Add(finalName);

            var relPath = $"Resources/{finalName}";
            WriteFileAndStage(relPath, f.ContentBytes);

            // Create symlink in Output/ -> ../Resources/<finalName>
            await CreateSymlinkAsync(Path.Combine(outputDir, finalName), $"../{relPath.Replace("\\", "/")}");
        }

        // Copy crew files into Resources/crew-<name>/
        foreach (var (file, crewName) in crewFiles)
        {
            if (file.ContentBytes == null || file.ContentBytes.Length == 0) continue;

            var baseName = Path.GetFileName(string.IsNullOrEmpty(file.RelativePath) ? Guid.NewGuid().ToString() : file.RelativePath);
            var safeCrew = crewName
                .Replace(" ", "-")
                .Replace("/", "-")
                .Replace("\\", "-");

            var finalName = baseName;
            if (usedFileNames.Contains(finalName))
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(baseName);
                var ext = Path.GetExtension(baseName);
                var counter = 2;
                while (usedFileNames.Contains($"{nameNoExt}_{counter}{ext}")) counter++;
                finalName = $"{nameNoExt}_{counter}{ext}";
                _logger.LogWarning("Crew file conflict for {Crew}. Renamed {Original} to {New}", crewName, baseName, finalName);
            }
            usedFileNames.Add(finalName);

            var relPath = $"Resources/crew-{safeCrew}/{finalName}";
            WriteFileAndStage(relPath, file.ContentBytes);

            // Create symlink in Output/ -> ../Resources/crew-<safeCrew>/<finalName>
            await CreateSymlinkAsync(Path.Combine(outputDir, finalName), $"../{relPath.Replace("\\", "/")}");
        }

        if (notebookFilesToAdd.Count > 0)
        {
            context.NotebookFiles.AddRange(notebookFilesToAdd);
            await context.SaveChangesAsync();
            _logger.LogInformation("Added {Count} notebook files to notebook {NotebookId}", notebookFilesToAdd.Count, notebookId);
        }
    }

    private static async Task<string> GenerateUniqueNotebookSlugAsync(ApplicationDbContext context, Guid projectId, string title, Guid? excludeNotebookId = null)
    {
        var baseSlug = SlugGenerator.Generate(title);
        var slug = baseSlug;
        var suffix = 2;
        while (await context.Notebooks.AnyAsync(n => n.ProjectId == projectId && n.Slug == slug && (!excludeNotebookId.HasValue || n.Id != excludeNotebookId.Value)))
        {
            slug = SlugGenerator.AddNumericSuffix(baseSlug, suffix++);
        }

        return slug;
    }

    private async Task CreateSymlinkAsync(string symlinkPath, string targetRelative)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Remove existing link or file if present
                if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
                {
                    try { File.Delete(symlinkPath); } catch { /* best-effort */ }
                }

                var result = await Cli.Wrap("ln")
                    .WithArguments(new[] { "-sf", targetRelative, symlinkPath })
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteAsync();

                if (result.ExitCode != 0)
                {
                    _logger.LogWarning("Symlink creation failed for {Symlink} -> {Target} (exit {Code})", symlinkPath, targetRelative, result.ExitCode);
                }
            }
            else
            {
                _logger.LogWarning("Symlink creation skipped (unsupported platform) for {Symlink}", symlinkPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating symlink {Symlink} -> {Target}", symlinkPath, targetRelative);
        }
    }
} 
