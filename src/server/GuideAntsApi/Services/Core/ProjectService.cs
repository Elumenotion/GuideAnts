using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Core;

public interface IProjectService
{
    Task<ProjectDto> CreateProjectAsync(CreateProjectDto createProjectDto);
    Task<ProjectDto?> GetProjectAsync(Guid projectId);
    Task<ProjectDetailsDto?> GetProjectDetailsAsync(Guid projectId);
    Task<IEnumerable<ProjectDto>> GetProjectsAsync();
    Task<ProjectDto?> UpdateProjectAsync(Guid projectId, UpdateProjectDto updateProjectDto);
    Task<bool> DeleteProjectAsync(Guid projectId);
    Task<ProjectDto?> CopyProjectAsync(Guid projectId);
    Task<bool> SetHomePageAsync(Guid projectId, Guid contentFileId);
    Task<bool> ClearHomePageAsync(Guid projectId);
}

public class ProjectService(IServiceScopeFactory scopeFactory, IConfiguration configuration, IStoragePathResolver pathResolver, ILogger<ProjectService> logger) : IProjectService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly IStoragePathResolver _pathResolver = pathResolver;
    private readonly ILogger<ProjectService> _logger = logger;

    // Backward-compatible overload used by tests.
    public ProjectService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ProjectService> logger)
        : this(
            scopeFactory,
            configuration,
            new LegacyStoragePathResolver(configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured")),
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

    private static ProjectDto ToProjectDto(Project project)
    {
        return new ProjectDto(
            project.Id,
            project.Title,
            project.Description,
            project.Created,
            project.HomePageContentFileId,
            true
        );
    }

    private static ProjectDetailsDto ToProjectDetailsDto(Project project, IReadOnlyDictionary<Guid, DateTime> lastActivityByNotebook)
    {
        return new ProjectDetailsDto(
            project.Id,
            project.Title,
            project.Description,
            project.Created,
            project.HomePageContentFileId,
            project.Notebooks.Select(n => {
                var guideId = n.GuideId ?? n.NotebookTemplateId ?? throw new InvalidOperationException("Notebook must have either GuideId or NotebookTemplateId");
                var lastActivity = lastActivityByNotebook.TryGetValue(n.Id, out var value) ? value : n.Created;
                return new NotebookDto(
                    n.Id,
                    n.Title,
                    n.Description,
                    guideId,
                    n.HomePageFileId,
                    lastActivity
                );
            }),
            project.ContentFiles.Select(cf => new ContentFileDto(
                cf.Id,
                cf.FileName,
                cf.Path
            )),
            project.Links.Select(l => new LinkDto(
                l.Id,
                l.Url
            )),
            project.SemiStructuredDatas.Select(sd => new SemiStructuredProjectDataDto(
                sd.Id,
                sd.Placeholder
            )),
            true
        );
    }

    public async Task<ProjectDto> CreateProjectAsync(CreateProjectDto createProjectDto)
    {
        using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var project = new Project
        {
            Title = createProjectDto.Title,
            Slug = await GenerateUniqueProjectSlugAsync(context, createProjectDto.Title),
            Description = createProjectDto.Description ?? string.Empty
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();

        var refreshedProject = await context.Projects
            .FirstOrDefaultAsync(p => p.Id == project.Id);

        return refreshedProject == null ? ToProjectDto(project) : ToProjectDto(refreshedProject);
    }

    public async Task<ProjectDto?> GetProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var project = await context.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && !p.Deleted);

        return project == null ? null : ToProjectDto(project);
    }

    public async Task<ProjectDetailsDto?> GetProjectDetailsAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var project = await context.Projects
            .Where(p => p.Id == projectId && !p.Deleted)
            .AsSingleQuery()
            .Include(p => p.Notebooks)
            .Include(p => p.ContentFiles)
            .Include(p => p.Links)
            .Include(p => p.SemiStructuredDatas)
            .FirstOrDefaultAsync();

        if (project == null)
            return null;

        // Use READ UNCOMMITTED for this read-only aggregation — dirty reads are acceptable
        // for display purposes and this prevents lock contention with retention cleanup.
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED");
        var lastActivityByNotebook = await context.UsageEvents
            .Where(e => e.ProjectId == projectId && e.NotebookId != null)
            .GroupBy(e => e.NotebookId!.Value)
            .Select(g => new { NotebookId = g.Key, LastActivity = g.Max(e => e.Created) })
            .ToDictionaryAsync(x => x.NotebookId, x => x.LastActivity);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED");

        return ToProjectDetailsDto(project, lastActivityByNotebook);
    }

    public async Task<IEnumerable<ProjectDto>> GetProjectsAsync()
    {
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var userLookupMs = phaseStopwatch.ElapsedMilliseconds;

        // Order by last activity using UsageEvents (indexed by ProjectId + Created).
        phaseStopwatch.Restart();
        var projects = await (
            from p in context.Projects
            where !p.Deleted && !p.IsSystemProject
            join ue in
                (
                    from e in context.UsageEvents
                    where e.ProjectId != null
                    group e by e.ProjectId into g
                    select new { ProjectId = g.Key!.Value, LastActivity = (DateTime?)g.Max(x => x.Created) }
                )
                on p.Id equals ue.ProjectId into ueLeft
            from ue in ueLeft.DefaultIfEmpty()
            orderby (ue.LastActivity ?? p.Created) descending
            select new
            {
                p.Id,
                p.Title,
                p.Description,
                p.Created,
                p.HomePageContentFileId
            }
        ).ToListAsync();
        var projectsQueryMs = phaseStopwatch.ElapsedMilliseconds;

        if (projects.Count == 0)
        {
            _logger.LogInformation(
                "GetProjectsAsync timing: userLookupMs={UserLookupMs}, projectsQueryMs={ProjectsQueryMs}, enrichmentMs={EnrichmentMs}, totalMs={TotalMs}, projectCount=0",
                userLookupMs, projectsQueryMs, 0, totalStopwatch.ElapsedMilliseconds);
            return [];
        }

        phaseStopwatch.Restart();
        var enrichmentMs = phaseStopwatch.ElapsedMilliseconds;

        _logger.LogInformation(
            "GetProjectsAsync timing: userLookupMs={UserLookupMs}, projectsQueryMs={ProjectsQueryMs}, enrichmentMs={EnrichmentMs}, totalMs={TotalMs}, projectCount={ProjectCount}",
            userLookupMs, projectsQueryMs, enrichmentMs, totalStopwatch.ElapsedMilliseconds, projects.Count);

        return projects.Select(p => new ProjectDto(
            p.Id,
            p.Title,
            p.Description,
            p.Created,
            p.HomePageContentFileId,
            true
        )).ToList();
    }

    public async Task<ProjectDto?> UpdateProjectAsync(Guid projectId, UpdateProjectDto updateProjectDto)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var project = await context.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId);

        if (project == null)
            return null;

        if (!string.IsNullOrWhiteSpace(updateProjectDto.Title))
        {
            var nextSlug = await GenerateUniqueProjectSlugAsync(context, updateProjectDto.Title, projectId);
            if (!string.Equals(project.Slug, nextSlug, StringComparison.Ordinal))
            {
                var oldRoot = _pathResolver.GetProjectRootPath(projectId);
                project.Slug = nextSlug;
                _pathResolver.InvalidateProject(projectId);
                var newRoot = _pathResolver.GetProjectRootPath(projectId);

                if (Directory.Exists(oldRoot) && !string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(newRoot))
                    {
                        throw new InvalidOperationException($"Cannot rename project folder to '{newRoot}' because it already exists.");
                    }

                    Directory.Move(oldRoot, newRoot);
                }
            }

            project.Title = updateProjectDto.Title;
        }
        project.Description = updateProjectDto.Description ?? project.Description;
        await context.SaveChangesAsync();

        return ToProjectDto(project);
    }

    public async Task<bool> DeleteProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var project = await context.Projects
            .Include(p => p.Notebooks)
            .Include(p => p.ContentFiles)
            .Include(p => p.Folders)
            .Include(p => p.ExternalAuths)
            .Include(p => p.Links)
            .Include(p => p.SemiStructuredDatas)
            .FirstOrDefaultAsync(p => p.Id == projectId);

        if (project == null)
            return false;

        // Single-user deployment behavior: always hard-delete and clean up filesystem.
        var projectRoot = _pathResolver.GetProjectRootPath(project.Id);
        if (Directory.Exists(projectRoot))
        {
            try
            {
                Directory.Delete(projectRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete project root at {ProjectRoot}", projectRoot);
            }
        }

        context.Notebooks.RemoveRange(project.Notebooks);
        context.ContentFiles.RemoveRange(project.ContentFiles);
        context.ProjectFolders.RemoveRange(project.Folders);
        context.ProjectExternalAuths.RemoveRange(project.ExternalAuths);
        context.Links.RemoveRange(project.Links);
        context.SemiStructuredProjectDatas.RemoveRange(project.SemiStructuredDatas);
        context.Projects.Remove(project);
        await context.SaveChangesAsync();
        _pathResolver.InvalidateProject(project.Id);
        return true;
    }

    public async Task<ProjectDto?> CopyProjectAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Use database transaction for atomicity
            using var transaction = await context.Database.BeginTransactionAsync();
            try
            {
                var sourceProject = await context.Projects
                .Include(p => p.Notebooks)
                    .ThenInclude(n => n.NotebookLinks)
                .Include(p => p.Notebooks)
                    .ThenInclude(n => n.NotebookSemiStructuredDatas)
                .Include(p => p.Notebooks)
                    .ThenInclude(n => n.Guide)
                .Include(p => p.Notebooks)
                    .ThenInclude(n => n.Conversations)
                        .ThenInclude(c => c.Messages)
                .Include(p => p.Notebooks)
                    .ThenInclude(n => n.NotebookFiles)
                .Include(p => p.ContentFiles)
                    .ThenInclude(cf => cf.Versions)
                        .ThenInclude(v => v.OriginVersion)
                .Include(p => p.ContentFiles)
                    .ThenInclude(cf => cf.Versions)
                        .ThenInclude(v => v.OriginNotebookFile)
                .Include(p => p.Links)
                .Include(p => p.SemiStructuredDatas)
                .Include(p => p.Folders)
                .Include(p => p.ExternalAuths)
                .Include(p => p.AssistantEnvironments)
                .FirstOrDefaultAsync(p => p.Id == projectId);

            if (sourceProject == null)
                return null;

            var newProject = new Project
            {
                Title = $"Copy of {sourceProject.Title}",
                Slug = await GenerateUniqueProjectSlugAsync(context, $"Copy of {sourceProject.Title}"),
                Description = sourceProject.Description
            };

            context.Projects.Add(newProject);
            await context.SaveChangesAsync();

            // Copy folders first (maintain hierarchy)
            var folderMap = new Dictionary<Guid, ProjectFolder>();
            var rootFolders = sourceProject.Folders.Where(f => f.ParentFolderId == null).ToList();
            await CopyFoldersRecursively(context, sourceProject.Folders, newProject.Id, folderMap, null);

            // Copy content files with all versions and shadow data
            var contentFileMap = new Dictionary<Guid, ContentFile>();
            var contentFileVersionMap = new Dictionary<Guid, ContentFileVersion>();
            await CopyContentFilesWithVersionsAndShadows(context, sourceProject.ContentFiles, newProject.Id, folderMap, contentFileMap, contentFileVersionMap);

            // Copy links
            var linkMap = new Dictionary<Guid, Link>();
            foreach (var link in sourceProject.Links)
            {
                var newLink = new Link
                {
                    Url = link.Url,
                    ProjectId = newProject.Id
                };
                context.Links.Add(newLink);
                linkMap[link.Id] = newLink;
            }
            await context.SaveChangesAsync();

            // Copy semi-structured data
            var semiStructuredDataMap = new Dictionary<Guid, SemiStructuredProjectData>();
            foreach (var data in sourceProject.SemiStructuredDatas)
            {
                var newData = new SemiStructuredProjectData
                {
                    Placeholder = data.Placeholder,
                    ProjectId = newProject.Id
                };
                context.SemiStructuredProjectDatas.Add(newData);
                semiStructuredDataMap[data.Id] = newData;
            }
            await context.SaveChangesAsync();

            // Copy external auth configurations (per-provider overrides)
            foreach (var ea in sourceProject.ExternalAuths)
            {
                var newEa = new ProjectExternalAuth
                {
                    ProjectId = newProject.Id,
                    ProviderId = ea.ProviderId,
                    AuthType = ea.AuthType,
                    ClientId = ea.ClientId,
                    Tenant = ea.Tenant,
                    HeaderName = ea.HeaderName,
                    HeaderValue = ea.HeaderValue
                };
                context.ProjectExternalAuths.Add(newEa);
            }
            await context.SaveChangesAsync();

            // Copy project-bounded guide/assistant environment configurations.
            foreach (var environment in sourceProject.AssistantEnvironments)
            {
                context.ProjectAssistantEnvironments.Add(new ProjectAssistantEnvironment
                {
                    ProjectId = newProject.Id,
                    AssistantId = environment.AssistantId,
                    EnvironmentConfigJson = environment.EnvironmentConfigJson,
                    Created = DateTime.UtcNow
                });
            }
            await context.SaveChangesAsync();

            // Copy notebooks with conversations and files (single user only)
            var notebookMap = new Dictionary<Guid, Notebook>();
            var notebookFileMap = new Dictionary<Guid, NotebookFile>();
            await CopyNotebooksWithConversationsAndFiles(context, sourceProject.Notebooks, sourceProject.Id, newProject.Id, notebookMap, linkMap, semiStructuredDataMap, contentFileVersionMap, notebookFileMap);

            // Copy notebook links and semi-structured data associations
            foreach (var notebook in sourceProject.Notebooks)
            {
                var newNotebook = notebookMap[notebook.Id];

                foreach (var nl in notebook.NotebookLinks)
                {
                    if (linkMap.TryGetValue(nl.LinkId, out var newLink))
                    {
                        var newNl = new NotebookLink
                        {
                            NotebookId = newNotebook.Id,
                            LinkId = newLink.Id
                        };
                        context.NotebookLinks.Add(newNl);
                    }
                }

                foreach (var nsd in notebook.NotebookSemiStructuredDatas)
                {
                    if (semiStructuredDataMap.TryGetValue(nsd.SemiStructuredProjectDataId, out var newData))
                    {
                        var newNsd = new NotebookSemiStructuredData
                        {
                            NotebookId = newNotebook.Id,
                            SemiStructuredProjectDataId = newData.Id
                        };
                        context.NotebookSemiStructuredDatas.Add(newNsd);
                    }
                }
            }
            await context.SaveChangesAsync();

            // Copy project home page
            if (sourceProject.HomePageContentFileId.HasValue && contentFileMap.TryGetValue(sourceProject.HomePageContentFileId.Value, out var newHomePageFile))
            {
                newProject.HomePageContentFileId = newHomePageFile.Id;
            }

            // Save all changes before committing transaction
            await context.SaveChangesAsync();

            // Commit transaction
            await transaction.CommitAsync();

            var refreshedProject = await context.Projects
                .FirstOrDefaultAsync(p => p.Id == newProject.Id);

            return refreshedProject == null ? null : ToProjectDto(refreshedProject);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    public async Task<bool> SetHomePageAsync(Guid projectId, Guid contentFileId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);

        var project = await context.Projects.FirstOrDefaultAsync(p => p.Id == projectId && !p.Deleted);
        if (project == null)
            return false;

        project.HomePageContentFileId = contentFileId;
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ClearHomePageAsync(Guid projectId)
    {

using var scope = CreateDbScope();
        var context = GetDbContext(scope);
        
        var project = await context.Projects.FirstOrDefaultAsync(p => p.Id == projectId && !p.Deleted);
        if (project == null)
            return false;
            
        project.HomePageContentFileId = null;
        await context.SaveChangesAsync();
        return true;
    }

    private async Task CopyFoldersRecursively(ApplicationDbContext context, ICollection<ProjectFolder> sourceFolders, Guid newProjectId, Dictionary<Guid, ProjectFolder> folderMap, Guid? parentFolderId)
    {
        var foldersToProcess = sourceFolders.Where(f => f.ParentFolderId == parentFolderId).ToList();
        
        foreach (var sourceFolder in foldersToProcess)
        {
            var newFolder = new ProjectFolder
            {
                Name = sourceFolder.Name,
                RelativePath = sourceFolder.RelativePath,
                ProjectId = newProjectId,
                ParentFolderId = parentFolderId
            };
            
            context.ProjectFolders.Add(newFolder);
            await context.SaveChangesAsync();
            
            folderMap[sourceFolder.Id] = newFolder;
            
            // Recursively copy child folders
            await CopyFoldersRecursively(context, sourceFolders, newProjectId, folderMap, newFolder.Id);
        }
    }

    private async Task CopyContentFilesWithVersionsAndShadows(ApplicationDbContext context, ICollection<ContentFile> sourceFiles, Guid newProjectId, Dictionary<Guid, ProjectFolder> folderMap, Dictionary<Guid, ContentFile> contentFileMap, Dictionary<Guid, ContentFileVersion> contentFileVersionMap)
    {
        foreach (var sourceFile in sourceFiles)
        {
            var newContentFile = new ContentFile
            {
                FileName = sourceFile.FileName,
                Path = sourceFile.Path,
                RelativePath = sourceFile.RelativePath,
                FileSize = sourceFile.FileSize,
                ContentType = sourceFile.ContentType,
                ProjectId = newProjectId,
                FolderId = sourceFile.FolderId.HasValue && folderMap.TryGetValue(sourceFile.FolderId.Value, out var newFolder) ? newFolder.Id : null,
                LatestVersion = sourceFile.LatestVersion,
                IsSnapshot = sourceFile.IsSnapshot
            };
            
            // Generate DocumentId after creation
            newContentFile.GenerateDocumentId();
            
            context.ContentFiles.Add(newContentFile);
            await context.SaveChangesAsync();
            contentFileMap[sourceFile.Id] = newContentFile;

            // Copy all versions
            foreach (var sourceVersion in sourceFile.Versions)
            {
                var newVersion = new ContentFileVersion
                {
                    ContentFileId = newContentFile.Id,
                    VersionNumber = sourceVersion.VersionNumber,
                    FileName = sourceVersion.FileName,
                    ContentHash = sourceVersion.ContentHash,
                    StoragePath = sourceVersion.StoragePath,
                    OriginalRelativePath = sourceVersion.OriginalRelativePath,
                    OriginalFolderId = sourceVersion.OriginalFolderId.HasValue && folderMap.TryGetValue(sourceVersion.OriginalFolderId.Value, out var newOriginalFolder) ? newOriginalFolder.Id : null,
                    Path = sourceVersion.Path,
                    RelativePath = sourceVersion.RelativePath,
                    FileSize = sourceVersion.FileSize,
                    ContentType = sourceVersion.ContentType,
                    Indexed = sourceVersion.Indexed,
                    FromNotebook = sourceVersion.FromNotebook,
                    OriginNotebookId = sourceVersion.OriginNotebookId,
                    OriginNotebookFileId = sourceVersion.OriginNotebookFileId
                };
                
                context.ContentFileVersions.Add(newVersion);
                await context.SaveChangesAsync();
                contentFileVersionMap[sourceVersion.Id] = newVersion;
            }

            // Copy markdown shadows for the latest version
            var latestVersion = sourceFile.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (latestVersion != null && contentFileVersionMap.TryGetValue(latestVersion.Id, out var newLatestVersion))
            {
                var sourceShadow = await context.ContentFileMarkdownShadows
                    .FirstOrDefaultAsync(s => s.OriginalContentFileVersionId == latestVersion.Id);
                
                if (sourceShadow != null)
                {
                    var newShadow = new ContentFileMarkdownShadow
                    {
                        OriginalContentFileVersionId = newLatestVersion.Id,
                        ContentHash = sourceShadow.ContentHash,
                        StoragePath = sourceShadow.StoragePath,
                        FileSize = sourceShadow.FileSize,
                        Status = sourceShadow.Status,
                        ErrorMessage = sourceShadow.ErrorMessage,
                        ProcessedAt = sourceShadow.ProcessedAt,
                        IsIndexed = sourceShadow.IsIndexed
                    };
                    
                    context.ContentFileMarkdownShadows.Add(newShadow);
                }
            }
        }
        
        await context.SaveChangesAsync();
    }

    private async Task CopyNotebooksWithConversationsAndFiles(ApplicationDbContext context, ICollection<Notebook> sourceNotebooks, Guid sourceProjectId, Guid newProjectId, Dictionary<Guid, Notebook> notebookMap, Dictionary<Guid, Link> linkMap, Dictionary<Guid, SemiStructuredProjectData> semiStructuredDataMap, Dictionary<Guid, ContentFileVersion> contentFileVersionMap, Dictionary<Guid, NotebookFile> notebookFileMap)
    {
        foreach (var sourceNotebook in sourceNotebooks)
        {
            var newNotebook = new Notebook
            {
                Title = sourceNotebook.Title,
                Slug = await GenerateUniqueNotebookSlugAsync(context, newProjectId, sourceNotebook.Title),
                Description = sourceNotebook.Description,
                ProjectId = newProjectId,
                GuideId = sourceNotebook.GuideId,
                SourceNotebookId = sourceNotebook.Id // Track the source for template initialization
            };
            
            context.Notebooks.Add(newNotebook);
            await context.SaveChangesAsync();
            notebookMap[sourceNotebook.Id] = newNotebook;

            // Copy conversations (single user only - reassign all messages to current user)
            foreach (var sourceConversation in sourceNotebook.Conversations)
            {
                var newConversation = new NotebookConversation
                {
                    Title = sourceConversation.Title,
                    Summary = sourceConversation.Summary,
                    NotebookId = newNotebook.Id
                };
                
                context.NotebookConversations.Add(newConversation);
                await context.SaveChangesAsync();

                // Copy conversation turns first (if any exist)
                var turnMap = new Dictionary<(Guid, int), ConversationTurn>();
                if (sourceConversation.Turns.Any())
                {
                    foreach (var sourceTurn in sourceConversation.Turns.OrderBy(t => t.TurnIndex))
                    {
                        var newTurn = new ConversationTurn
                        {
                            NotebookConversationId = newConversation.Id,
                            TurnIndex = sourceTurn.TurnIndex,
                            AssistantName = sourceTurn.AssistantName,
                            ModelDeploymentId = sourceTurn.ModelDeploymentId,
                            Instructions = sourceTurn.Instructions,
                            ChatRunOutputJson = sourceTurn.ChatRunOutputJson,
                            UsageJson = sourceTurn.UsageJson,
                            FilesCreated = sourceTurn.FilesCreated,
                            FilesModified = sourceTurn.FilesModified,
                            WorkingDirectory = sourceTurn.WorkingDirectory
                        };
                        
                        context.ConversationTurns.Add(newTurn);
                        turnMap[(sourceTurn.NotebookConversationId, sourceTurn.TurnIndex)] = newTurn;
                    }
                    await context.SaveChangesAsync();
                }

                // Copy messages, reassigning all to current user
                // First, collect all unique turn indices from messages
                var messageTurnIndices = sourceConversation.Messages
                    .Where(m => m.TurnIndex > 0)
                    .Select(m => m.TurnIndex)
                    .Distinct()
                    .ToList();

                // Create any missing turns for messages
                foreach (var turnIndex in messageTurnIndices)
                {
                    if (!turnMap.ContainsKey((newConversation.Id, turnIndex)))
                    {
                        var defaultTurn = new ConversationTurn
                        {
                            NotebookConversationId = newConversation.Id,
                            TurnIndex = turnIndex,
                            AssistantName = "Unknown",
                            Instructions = "Copied from template"
                        };
                        
                        context.ConversationTurns.Add(defaultTurn);
                        turnMap[(newConversation.Id, turnIndex)] = defaultTurn;
                    }
                }

                // Save any new turns before creating messages
                if (messageTurnIndices.Any())
                {
                    await context.SaveChangesAsync();
                }

                // Now create the messages
                foreach (var sourceMessage in sourceConversation.Messages.OrderBy(m => m.Created))
                {
                    var newMessage = new NotebookConversationMessage
                    {
                        Content = UpdateContentUrls(sourceMessage.Content, sourceProjectId, newProjectId, sourceNotebook.Id, newNotebook.Id),
                        Role = sourceMessage.Role,
                        NotebookConversationId = newConversation.Id,
                        UserId = Guid.Empty,
                        AssistantName = sourceMessage.AssistantName,
                        ModelDeploymentId = sourceMessage.ModelDeploymentId,
                        ToolCalls = sourceMessage.ToolCalls,
                        FunctionName = sourceMessage.FunctionName,
                        ToolCallId = sourceMessage.ToolCallId,
                        TurnIndex = sourceMessage.TurnIndex,
                        MessageSequence = sourceMessage.MessageSequence,
                        MessageContentType = sourceMessage.MessageContentType,
                        IsEdited = sourceMessage.IsEdited,
                        LastEditedByUserId = sourceMessage.LastEditedByUserId,
                        LastEditedAt = sourceMessage.LastEditedAt,
                        IsStreaming = sourceMessage.IsStreaming
                    };
                    
                    context.NotebookConversationMessages.Add(newMessage);
                }
            }
            
            // Copy notebook files (both database records and physical files)
            CopyNotebookFiles(context, sourceNotebook.NotebookFiles, newNotebook, contentFileVersionMap, notebookFileMap);
            
            // Copy notebook home page
            if (sourceNotebook.HomePageFileId.HasValue && notebookFileMap.TryGetValue(sourceNotebook.HomePageFileId.Value, out var newHomePageFile))
            {
                newNotebook.HomePageFileId = newHomePageFile.Id;
            }
            
            await context.SaveChangesAsync();
        }
    }

    private void CopyNotebookFiles(ApplicationDbContext context, ICollection<NotebookFile> sourceFiles, Notebook newNotebook, Dictionary<Guid, ContentFileVersion> contentFileVersionMap, Dictionary<Guid, NotebookFile> notebookFileMap)
    {
        var storagePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        
        foreach (var sourceFile in sourceFiles)
        {
            // Create database record
            var newNotebookFile = new NotebookFile
            {
                NotebookId = newNotebook.Id,
                RelativePath = sourceFile.RelativePath,
                FileSize = sourceFile.FileSize,
                LastModifiedUtc = sourceFile.LastModifiedUtc,
                FileHash = sourceFile.FileHash,
                OriginContentFileVersionId = sourceFile.OriginContentFileVersionId.HasValue && contentFileVersionMap.TryGetValue(sourceFile.OriginContentFileVersionId.Value, out var newVersion) ? newVersion.Id : null
            };
            
            // Generate DocumentId after setting the properties
            newNotebookFile.GenerateDocumentId(newNotebook.Id);
            
            context.NotebookFiles.Add(newNotebookFile);
            notebookFileMap[sourceFile.Id] = newNotebookFile;
            
            // Copy physical file
            var sourceProjectId = sourceFile.Notebook.ProjectId;
            var sourceNotebookId = sourceFile.NotebookId;
            
            var sourceFilePath = Path.Combine(_pathResolver.GetNotebookRootPath(sourceProjectId, sourceNotebookId), sourceFile.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            var destFilePath = Path.Combine(_pathResolver.GetNotebookRootPath(newNotebook.ProjectId, newNotebook.Id), sourceFile.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            
            // Create destination directory if it doesn't exist
            var destDirectory = Path.GetDirectoryName(destFilePath);
            if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
            {
                Directory.CreateDirectory(destDirectory);
            }
            
            // Copy the file if it exists
            if (File.Exists(sourceFilePath))
            {
                File.Copy(sourceFilePath, destFilePath, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Updates URLs in message content to point to the new project and notebook
    /// </summary>
    private static string UpdateContentUrls(string content, Guid sourceProjectId, Guid newProjectId, Guid sourceNotebookId, Guid newNotebookId)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Pattern to match URLs like: /api/projects/{projectId}/notebooks/{notebookId}/files/content?path=...
        var urlPattern = $@"/api/projects/{sourceProjectId}/notebooks/{sourceNotebookId}/files/content";
        var replacement = $"/api/projects/{newProjectId}/notebooks/{newNotebookId}/files/content";
        
        return content.Replace(urlPattern, replacement);
    }

    private static async Task<string> GenerateUniqueProjectSlugAsync(ApplicationDbContext context, string title, Guid? excludeProjectId = null)
    {
        var baseSlug = SlugGenerator.Generate(title);
        var slug = baseSlug;
        var suffix = 2;

        while (await context.Projects.AnyAsync(p => p.Slug == slug && (!excludeProjectId.HasValue || p.Id != excludeProjectId.Value)))
        {
            slug = SlugGenerator.AddNumericSuffix(baseSlug, suffix++);
        }

        return slug;
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
}
