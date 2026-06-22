using System.IO.Compression;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class GuideAntsSystemSeeder : IGuideAntsSystemSeeder
{
    internal const string BootstrapRootRelativePath = "Resources/bootstrap/guides";
    internal const string SystemProjectTitle = "GuideAnts System";
    internal const string SystemProjectSlug = "guideants-system";
    internal const string UserGuideFolderName = "guideants-guide";
    internal const string AdminGuideFolderName = "guideants-guide-admin";
    internal const string UserGuideName = "GuideAnts Guide";
    internal const string AdminGuideName = "GuideAnts Guide Admin";

    private readonly IWebHostEnvironment _environment;
    private readonly ApplicationDbContext _dbContext;
    private readonly IGuideExportImportService _guideExportImportService;
    private readonly IGuideAntsSystemSettingsStore _settingsStore;
    private readonly InternalPublishedGuideFactory _publishedGuideFactory;
    private readonly ILogger<GuideAntsSystemSeeder> _logger;

    public GuideAntsSystemSeeder(
        IWebHostEnvironment environment,
        ApplicationDbContext dbContext,
        IGuideExportImportService guideExportImportService,
        IGuideAntsSystemSettingsStore settingsStore,
        ILogger<GuideAntsSystemSeeder> logger)
    {
        _environment = environment;
        _dbContext = dbContext;
        _guideExportImportService = guideExportImportService;
        _settingsStore = settingsStore;
        _publishedGuideFactory = new InternalPublishedGuideFactory(dbContext);
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var currentSettings = await _settingsStore.GetAsync(cancellationToken);

        var project = await ResolveSystemProjectAsync(currentSettings, cancellationToken);
        if (project == null)
        {
            project = await CreateSystemProjectAsync(cancellationToken);
            _logger.LogInformation(
                "Created GuideAnts system project {ProjectId} (slug {Slug}).",
                project.Id,
                project.Slug);
        }

        var userGuideId = await EnsureGuideAsync(
            UserGuideFolderName,
            UserGuideName,
            currentSettings?.UserGuideId,
            cancellationToken);
        var adminGuideId = await EnsureGuideAsync(
            AdminGuideFolderName,
            AdminGuideName,
            currentSettings?.AdminGuideId,
            cancellationToken);

        var userNotebook = await EnsureNotebookAsync(
            project.Id,
            userGuideId,
            UserGuideName,
            currentSettings?.UserNotebookId,
            cancellationToken);
        var adminNotebook = await EnsureNotebookAsync(
            project.Id,
            adminGuideId,
            AdminGuideName,
            currentSettings?.AdminNotebookId,
            cancellationToken);

        var userPublishedGuide = await _publishedGuideFactory.EnsureAppIdentityPublishedGuideAsync(
            project.Id,
            userGuideId,
            userNotebook.Id,
            UserGuideName,
            cancellationToken);
        var adminPublishedGuide = await _publishedGuideFactory.EnsureAppIdentityPublishedGuideAsync(
            project.Id,
            adminGuideId,
            adminNotebook.Id,
            AdminGuideName,
            cancellationToken);

        await _settingsStore.SaveAsync(
            new GuideAntsSystemSettings
            {
                ProjectId = project.Id,
                UserGuideId = userGuideId,
                AdminGuideId = adminGuideId,
                UserNotebookId = userNotebook.Id,
                AdminNotebookId = adminNotebook.Id,
                UserPublishedGuideId = userPublishedGuide.Id,
                AdminPublishedGuideId = adminPublishedGuide.Id,
                ClientBridgeId = GuideAntsSystemSettings.DefaultClientBridgeId
            },
            cancellationToken);

        _logger.LogInformation(
            "GuideAnts system bootstrap verified: project={ProjectId}, userPub={UserPublishedGuideId}, adminPub={AdminPublishedGuideId}.",
            project.Id,
            userPublishedGuide.Id,
            adminPublishedGuide.Id);
    }

    private async Task<Project?> ResolveSystemProjectAsync(
        GuideAntsSystemSettings? settings,
        CancellationToken cancellationToken)
    {
        if (settings?.ProjectId is Guid configuredProjectId)
        {
            var configuredProject = await _dbContext.Projects
                .FirstOrDefaultAsync(
                    p => p.Id == configuredProjectId && !p.Deleted,
                    cancellationToken);

            if (configuredProject != null)
            {
                EnsureSystemProjectFlags(configuredProject);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return configuredProject;
            }
        }

        var slugProject = await _dbContext.Projects
            .FirstOrDefaultAsync(
                p => p.Slug == SystemProjectSlug && !p.Deleted,
                cancellationToken);

        if (slugProject != null)
        {
            EnsureSystemProjectFlags(slugProject);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return slugProject;
        }

        return null;
    }

    private async Task<Project> CreateSystemProjectAsync(CancellationToken cancellationToken)
    {
        var duplicateSystemProject = await _dbContext.Projects
            .AnyAsync(p => p.IsSystemProject && !p.Deleted, cancellationToken);

        if (duplicateSystemProject)
        {
            throw new InvalidOperationException(
                "Multiple system projects detected; expected exactly one IsSystemProject row.");
        }

        var slugTaken = await _dbContext.Projects
            .AnyAsync(p => p.Slug == SystemProjectSlug && !p.Deleted, cancellationToken);

        if (slugTaken)
        {
            throw new InvalidOperationException(
                $"Project slug '{SystemProjectSlug}' is already in use by a non-system project.");
        }

        var project = new Project
        {
            Title = SystemProjectTitle,
            Slug = SystemProjectSlug,
            Description = "Hidden system project for in-app GuideAnts guides.",
            IsSystemProject = true,
            Created = DateTime.UtcNow
        };

        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return project;
    }

    private static void EnsureSystemProjectFlags(Project project)
    {
        project.IsSystemProject = true;

        if (!string.Equals(project.Slug, SystemProjectSlug, StringComparison.OrdinalIgnoreCase))
        {
            project.Slug = SystemProjectSlug;
        }
    }

    private async Task<Guid> EnsureGuideAsync(
        string folderName,
        string guideName,
        Guid? preferredGuideId,
        CancellationToken cancellationToken)
    {
        var guideFolder = Path.Combine(
            _environment.ContentRootPath,
            BootstrapRootRelativePath,
            folderName);

        if (!Directory.Exists(guideFolder))
        {
            throw new InvalidOperationException(
                $"Bootstrap guide folder not found at '{guideFolder}'.");
        }

        if (preferredGuideId.HasValue)
        {
            var preferredGuide = await _dbContext.Assistants
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.Id == preferredGuideId.Value
                         && a.Kind == AssistantKind.Guide
                         && a.IsActive,
                    cancellationToken);

            if (preferredGuide != null)
            {
                await SyncBootstrapGuideFromFolderAsync(guideFolder, guideName, folderName, cancellationToken);
                return preferredGuide.Id;
            }
        }

        var existingGuide = await _dbContext.Assistants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Name == guideName && a.Kind == AssistantKind.Guide && a.IsActive,
                cancellationToken);

        if (existingGuide != null)
        {
            await SyncBootstrapGuideFromFolderAsync(guideFolder, guideName, folderName, cancellationToken);
            return existingGuide.Id;
        }

        await using var stream = await OpenFolderAsZipStreamAsync(guideFolder, cancellationToken);
        var importResult = await _guideExportImportService.ImportGuideAsync(stream);

        if (!importResult.Success || !importResult.GuideId.HasValue)
        {
            throw new InvalidOperationException(
                $"Failed to import bootstrap guide '{guideName}' from '{folderName}'.");
        }

        _logger.LogInformation(
            "Imported bootstrap guide '{GuideName}' ({GuideId}) from {FolderName}.",
            guideName,
            importResult.GuideId.Value,
            folderName);

        return importResult.GuideId.Value;
    }

    private async Task SyncBootstrapGuideFromFolderAsync(
        string guideFolder,
        string guideName,
        string folderName,
        CancellationToken cancellationToken)
    {
        await using var stream = await OpenFolderAsZipStreamAsync(guideFolder, cancellationToken);
        var importResult = await _guideExportImportService.ImportGuideAsync(stream);

        if (!importResult.Success || !importResult.GuideId.HasValue)
        {
            throw new InvalidOperationException(
                $"Failed to sync bootstrap guide '{guideName}' from '{folderName}'.");
        }

        _logger.LogInformation(
            "Synced bootstrap guide '{GuideName}' ({GuideId}) from {FolderName}.",
            guideName,
            importResult.GuideId.Value,
            folderName);
    }

    private async Task<Notebook> EnsureNotebookAsync(
        Guid projectId,
        Guid guideId,
        string guideName,
        Guid? preferredNotebookId,
        CancellationToken cancellationToken)
    {
        if (preferredNotebookId.HasValue)
        {
            var preferredNotebook = await _dbContext.Notebooks
                .FirstOrDefaultAsync(
                    n => n.Id == preferredNotebookId.Value && n.ProjectId == projectId,
                    cancellationToken);

            if (preferredNotebook != null)
            {
                if (preferredNotebook.GuideId != guideId)
                {
                    preferredNotebook.GuideId = guideId;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                return preferredNotebook;
            }
        }

        var existingNotebook = await _dbContext.Notebooks
            .FirstOrDefaultAsync(
                n => n.ProjectId == projectId && n.GuideId == guideId,
                cancellationToken);

        if (existingNotebook != null)
        {
            return existingNotebook;
        }

        var notebook = new Notebook
        {
            ProjectId = projectId,
            Title = guideName,
            Slug = await GenerateUniqueNotebookSlugAsync(projectId, guideName, cancellationToken),
            Description = $"System notebook for {guideName}",
            GuideId = guideId,
            Created = DateTime.UtcNow
        };

        _dbContext.Notebooks.Add(notebook);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return notebook;
    }

    private async Task<string> GenerateUniqueNotebookSlugAsync(
        Guid projectId,
        string title,
        CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(title);
        var slug = baseSlug;
        var suffix = 1;

        while (await _dbContext.Notebooks.AnyAsync(
                   n => n.ProjectId == projectId && n.Slug == slug,
                   cancellationToken))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    private static string Slugify(string title)
    {
        var chars = title
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private static async Task<MemoryStream> OpenFolderAsZipStreamAsync(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(folderPath, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var sourceStream = File.OpenRead(filePath);
                await sourceStream.CopyToAsync(entryStream, cancellationToken);
            }
        }

        archiveStream.Position = 0;
        return archiveStream;
    }
}
