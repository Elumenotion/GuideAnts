using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public class ProjectFolderService : IProjectFolderService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly string _storagePath;
    private readonly IStoragePathResolver _pathResolver;

    public ProjectFolderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IStoragePathResolver pathResolver)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _storagePath = _configuration["FileStorage:Path"] ?? 
            throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    // Backward-compatible overload used by tests.
    public ProjectFolderService(IServiceScopeFactory scopeFactory, IConfiguration configuration)
        : this(
            scopeFactory,
            configuration,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")))
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

    private static ProjectFolderDto ToProjectFolderDto(ProjectFolder folder, int fileCount = 0, int subFolderCount = 0)
    {
        return new ProjectFolderDto(
            folder.Id,
            folder.Name,
            folder.RelativePath,
            folder.ParentFolderId,
            folder.Created,
            folder.Modified,
            fileCount,
            subFolderCount
        );
    }

    private static FolderTreeDto ToFolderTreeDto(ProjectFolder folder, List<FolderTreeDto> subFolders, List<ContentFileDetailsDto> files)
    {
        return new FolderTreeDto(
            folder.Id,
            folder.Name,
            folder.RelativePath,
            subFolders,
            files
        );
    }

    private static ContentFileDetailsDto ToContentFileDetailsDto(ContentFile file)
    {
        return new ContentFileDetailsDto(
            file.Id,
            file.FileName,
            file.Path,
            file.RelativePath,
            file.ContentType,
                            false, // Index removed
            file.DocumentId,
            file.Created,
            file.FileSize,
            file.FolderId,
            file.Folder?.RelativePath,
            file.LatestVersion,
            file.IsSnapshot,

            // Markdown fields - will be populated by enhanced method when needed
            false,
            null,
            null
        );
    }

    public async Task<ProjectFolderDto> CreateFolderAsync(Guid projectId, CreateFolderDto dto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        // Validate folder name
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid folder name");
        }

        // Calculate relative path
        string relativePath = dto.Name;
        ProjectFolder? parentFolder = null;
        
        if (dto.ParentFolderId.HasValue)
        {
            parentFolder = await context.ProjectFolders
                .FirstOrDefaultAsync(f => f.Id == dto.ParentFolderId && f.ProjectId == projectId);
            
            if (parentFolder == null)
            {
                throw new ArgumentException("Parent folder not found");
            }
            
            relativePath = string.IsNullOrEmpty(parentFolder.RelativePath) 
                ? dto.Name 
                : $"{parentFolder.RelativePath}/{dto.Name}";
        }

        // Check for duplicate path
        var existingFolder = await context.ProjectFolders
            .FirstOrDefaultAsync(f => f.RelativePath == relativePath && f.ProjectId == projectId);
        
        if (existingFolder != null)
        {
            throw new ArgumentException("Folder already exists at this path");
        }

        // Create physical directory
        var physicalPath = Path.Combine(_pathResolver.GetProjectRootPath(projectId), relativePath);
        Directory.CreateDirectory(physicalPath);

        // Create database record
        var folder = new ProjectFolder
        {
            Name = dto.Name,
            RelativePath = relativePath,
            ProjectId = projectId,
            ParentFolderId = dto.ParentFolderId
        };

        context.ProjectFolders.Add(folder);
        await context.SaveChangesAsync();

        return ToProjectFolderDto(folder, 0, 0);
    }

    public async Task<ProjectFolderDto?> UpdateFolderAsync(Guid projectId, Guid folderId, UpdateFolderDto dto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Include(f => f.SubFolders)
            .Include(f => f.ContentFiles)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);

        if (folder == null)
        {
            return null;
        }

        var oldPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        var oldRelativePath = folder.RelativePath;
        bool pathChanged = false;

        // Update name if provided
        if (!string.IsNullOrWhiteSpace(dto.Name) && dto.Name != folder.Name)
        {
            if (dto.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Invalid folder name");
            }

            folder.Name = dto.Name;
            pathChanged = true;
        }

        // Update parent if provided
        if (dto.ParentFolderId != folder.ParentFolderId)
        {
            // Validate parent folder exists and prevent circular references
            if (dto.ParentFolderId.HasValue)
            {
                var newParent = await context.ProjectFolders
                    .FirstOrDefaultAsync(f => f.Id == dto.ParentFolderId && f.ProjectId == projectId);

                if (newParent == null)
                {
                    throw new ArgumentException("Parent folder not found");
                }

                if (!folder.CanMoveTo(newParent))
                {
                    throw new ArgumentException("Cannot move folder to a subfolder of itself");
                }
            }

            folder.ParentFolderId = dto.ParentFolderId;
            pathChanged = true;
        }

        if (pathChanged)
        {
            // Update relative path
            var parentFolder = dto.ParentFolderId.HasValue 
                ? await context.ProjectFolders.FirstOrDefaultAsync(f => f.Id == dto.ParentFolderId)
                : null;
            
            folder.UpdateRelativePath(parentFolder);

            // Check for path conflicts
            var conflictingFolder = await context.ProjectFolders
                .FirstOrDefaultAsync(f => f.RelativePath == folder.RelativePath && f.ProjectId == projectId && f.Id != folderId);
            
            if (conflictingFolder != null)
            {
                throw new ArgumentException("A folder already exists at this path");
            }

            // Move physical directory
            var newPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
            if (Directory.Exists(oldPhysicalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                Directory.Move(oldPhysicalPath, newPhysicalPath);
            }

            // Update all descendant folders and files
            await UpdateDescendantPaths(context, folder, oldRelativePath);
        }

        await context.SaveChangesAsync();

        var fileCount = await context.ContentFiles.CountAsync(f => f.FolderId == folderId);
        var subFolderCount = await context.ProjectFolders.CountAsync(f => f.ParentFolderId == folderId);

        return ToProjectFolderDto(folder, fileCount, subFolderCount);
    }

    public async Task<bool> DeleteFolderAsync(Guid projectId, Guid folderId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Include(f => f.SubFolders)
            .Include(f => f.ContentFiles)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);

        if (folder == null)
        {
            return false;
        }

        // Only allow deletion of empty folders
        if (!folder.IsEmpty())
        {
            throw new InvalidOperationException("Cannot delete non-empty folder");
        }

        // Delete physical directory if it exists
        var physicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        if (Directory.Exists(physicalPath))
        {
            Directory.Delete(physicalPath);
        }

        context.ProjectFolders.Remove(folder);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<IEnumerable<ProjectFolderDto>> GetFoldersAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folders = await context.ProjectFolders
            .Where(f => f.ProjectId == projectId)
            .Select(f => new { Folder = f, FileCount = f.ContentFiles.Count, SubFolderCount = f.SubFolders.Count })
            .ToListAsync();

        return folders.Select(f => ToProjectFolderDto(f.Folder, f.FileCount, f.SubFolderCount));
    }

    public async Task<FolderTreeDto> GetFolderTreeAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        // Retrieve the project name to use as the root folder title
        var projectName = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Title)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(projectName))
        {
            // Fallback to "Root" if the project cannot be found – should not normally happen
            projectName = "Root";
        }

        // Get all folders and files for the project
        var folders = await context.ProjectFolders
            .Where(f => f.ProjectId == projectId)
            .Include(f => f.SubFolders)
            .Include(f => f.ContentFiles)
            .ToListAsync();

        var files = await context.ContentFiles
            .Where(f => f.ProjectId == projectId)
            .Include(f => f.Folder)
            .ToListAsync();

        // Build tree structure
        return BuildFolderTree(folders, files, null, projectName);
    }

    public async Task<bool> MoveFolderAsync(Guid projectId, Guid folderId, Guid? newParentId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.Id == folderId && f.ProjectId == projectId);

        if (folder == null)
        {
            return false;
        }

        // Validate new parent
        ProjectFolder? newParent = null;
        if (newParentId.HasValue)
        {
            newParent = await context.ProjectFolders
                .FirstOrDefaultAsync(f => f.Id == newParentId && f.ProjectId == projectId);
            
            if (newParent == null)
            {
                return false;
            }

            if (!folder.CanMoveTo(newParent))
            {
                return false;
            }
        }

        var oldPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        var oldRelativePath = folder.RelativePath;

        // Update parent and path
        folder.ParentFolderId = newParentId;
        folder.UpdateRelativePath(newParent);

        // Check for path conflicts
        var conflictingFolder = await context.ProjectFolders
            .FirstOrDefaultAsync(f => f.RelativePath == folder.RelativePath && f.ProjectId == projectId && f.Id != folderId);
        
        if (conflictingFolder != null)
        {
            return false;
        }

        // Move physical directory
        var newPhysicalPath = folder.GetPhysicalPath(_pathResolver.GetProjectRootPath(projectId));
        if (Directory.Exists(oldPhysicalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
            Directory.Move(oldPhysicalPath, newPhysicalPath);
        }

        // Update descendant paths
        await UpdateDescendantPaths(context, folder, oldRelativePath);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<ProjectFolderDto?> GetFolderAsync(Guid projectId, Guid folderId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var folder = await context.ProjectFolders
            .Where(f => f.Id == folderId && f.ProjectId == projectId)
            .Select(f => new { Folder = f, FileCount = f.ContentFiles.Count, SubFolderCount = f.SubFolders.Count })
            .FirstOrDefaultAsync();

        return folder == null ? null : ToProjectFolderDto(folder.Folder, folder.FileCount, folder.SubFolderCount);
    }

    private async Task UpdateDescendantPaths(ApplicationDbContext context, ProjectFolder folder, string oldBasePath)
    {
        var descendants = await context.ProjectFolders
            .Where(f => f.ProjectId == folder.ProjectId && f.RelativePath.StartsWith(oldBasePath + "/"))
            .ToListAsync();

        foreach (var descendant in descendants)
        {
            var relativePart = descendant.RelativePath.Substring(oldBasePath.Length + 1);
            descendant.RelativePath = $"{folder.RelativePath}/{relativePart}";
            descendant.Modified = DateTime.UtcNow;
        }

        var descendantFiles = await context.ContentFiles
            .Where(f => f.ProjectId == folder.ProjectId && f.RelativePath.StartsWith(oldBasePath + "/"))
            .ToListAsync();

        foreach (var file in descendantFiles)
        {
            var relativePart = file.RelativePath.Substring(oldBasePath.Length + 1);
            file.RelativePath = $"{folder.RelativePath}/{relativePart}";
            
            // Update physical path
            var oldPhysicalPath = file.Path;
            var newPhysicalPath = Path.Combine(_pathResolver.GetProjectRootPath(folder.ProjectId), file.RelativePath);
            
            if (File.Exists(oldPhysicalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                File.Move(oldPhysicalPath, newPhysicalPath);
                file.Path = newPhysicalPath;
            }
        }
    }

    private static FolderTreeDto BuildFolderTree(List<ProjectFolder> allFolders, List<ContentFile> allFiles, Guid? parentId, string rootName)
    {
        var rootFolders = allFolders.Where(f => f.ParentFolderId == parentId).ToList();
        var rootFiles = allFiles.Where(f => f.FolderId == parentId).ToList();

        // Create virtual root folder for the project
        if (parentId == null)
        {
            var subFolders = rootFolders.Select(f => BuildFolderTreeNode(f, allFolders, allFiles)).ToList();
            var files = rootFiles.Select(ToContentFileDetailsDto).ToList();

            return new FolderTreeDto(
                Guid.Empty, // Virtual root
                rootName,
                "",
                subFolders,
                files
            );
        }

        var folder = allFolders.First(f => f.Id == parentId);
        return BuildFolderTreeNode(folder, allFolders, allFiles);
    }

    private static FolderTreeDto BuildFolderTreeNode(ProjectFolder folder, List<ProjectFolder> allFolders, List<ContentFile> allFiles)
    {
        var subFolders = allFolders
            .Where(f => f.ParentFolderId == folder.Id)
            .Select(f => BuildFolderTreeNode(f, allFolders, allFiles))
            .ToList();

        var files = allFiles
            .Where(f => f.FolderId == folder.Id)
            .Select(ToContentFileDetailsDto)
            .ToList();

        return ToFolderTreeDto(folder, subFolders, files);
    }
} 
