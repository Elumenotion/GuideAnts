using System.Text.Json;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Migrations;

public sealed class NamedStorageMigrationRunner
{
    private readonly string _connectionString;
    private readonly string _storageRoot;
    private readonly ILogger _logger;

    public NamedStorageMigrationRunner(string connectionString, string storageRoot, ILogger logger)
    {
        _connectionString = connectionString;
        _storageRoot = storageRoot;
        _logger = logger;
    }

    public async Task<int> RunAsync(bool apply, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Named storage migration started. Apply mode: {Apply}", apply);
        _logger.LogInformation("Storage root: {StorageRoot}", _storageRoot);

        if (!Directory.Exists(_storageRoot))
        {
            throw new DirectoryNotFoundException($"Storage root not found: {_storageRoot}");
        }

        await using var db = CreateDbContext();
        var projects = await db.Projects
            .AsNoTracking()
            .Select(p => new ProjectInfo(p.Id, p.Slug))
            .ToListAsync(cancellationToken);

        var notebooks = await db.Notebooks
            .AsNoTracking()
            .Select(n => new NotebookInfo(n.Id, n.ProjectId, n.Slug))
            .ToListAsync(cancellationToken);

        var notebooksByProject = notebooks
            .GroupBy(n => n.ProjectId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var totalPathUpdates = 0;
        var totalMoves = 0;
        var totalArchivedOrphans = 0;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            notebooksByProject.TryGetValue(project.Id, out var notebookList);
            notebookList ??= [];

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var performedMoves = new List<MoveOperation>();

            try
            {
                totalMoves += MigrateFilesystem(project, notebookList, apply, performedMoves);
                totalPathUpdates += await RewriteDatabasePathsAsync(db, project, notebookList, cancellationToken);

                if (apply)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                else
                {
                    await tx.RollbackAsync(cancellationToken);
                }
            }
            catch
            {
                if (apply)
                {
                    await tx.RollbackAsync(cancellationToken);
                    RollbackMoves(performedMoves);
                }

                throw;
            }
        }

        if (apply)
        {
            totalArchivedOrphans = ArchiveUnmappedLegacyDirectories(projects);
        }

        var validation = await ValidateNoLegacyReferencesAsync(db, projects, cancellationToken);
        if (!apply)
        {
            _logger.LogInformation(
                "Dry-run validation summary. Legacy DB references: {LegacyDbReferences}, Legacy FS references: {LegacyFsReferences}",
                validation.LegacyDbReferences,
                validation.LegacyFilesystemReferences);
        }
        else if (validation.LegacyDbReferences > 0 || validation.LegacyFilesystemReferences > 0)
        {
            throw new InvalidOperationException(
                $"Legacy references remain after migration. DB={validation.LegacyDbReferences}, FS={validation.LegacyFilesystemReferences}.");
        }

        _logger.LogInformation(
            "Named storage migration completed. Projects: {ProjectCount}, Filesystem moves: {Moves}, Updated DB paths: {PathUpdates}, Apply: {Apply}",
            projects.Count,
            totalMoves,
            totalPathUpdates,
            apply);
        if (apply)
        {
            _logger.LogInformation("Archived unmatched legacy GUID directories: {ArchivedOrphans}", totalArchivedOrphans);
        }

        return 0;
    }

    private ApplicationDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer(_connectionString, sql =>
        {
            sql.UseVectorSearch();
            sql.CommandTimeout(120);
        });

        return new ApplicationDbContext(optionsBuilder.Options);
    }

    private int MigrateFilesystem(
        ProjectInfo project,
        IReadOnlyCollection<NotebookInfo> notebooks,
        bool apply,
        List<MoveOperation> performedMoves)
    {
        var moveCount = 0;
        var oldProjectRoot = Path.Combine(_storageRoot, project.Id.ToString());
        var newProjectRoot = Path.Combine(_storageRoot, project.Slug);

        moveCount += MoveDirectoryIfNeeded(oldProjectRoot, newProjectRoot, apply, performedMoves);

        foreach (var notebook in notebooks)
        {
            var legacyNotebookRoot = Path.Combine(newProjectRoot, "notebooks", notebook.Id.ToString());
            var namedNotebookRoot = Path.Combine(newProjectRoot, notebook.Slug);
            moveCount += MoveDirectoryIfNeeded(legacyNotebookRoot, namedNotebookRoot, apply, performedMoves);

            if (Directory.Exists(namedNotebookRoot))
            {
                EnsureNotebookAssociationMetadata(namedNotebookRoot, project.Id, notebook.Id, apply);
            }
        }

        if (apply)
        {
            moveCount += ArchiveUnmappedNotebookDirectories(
                parentNotebookContainer: Path.Combine(newProjectRoot, "notebooks"),
                archiveParent: Path.Combine(_storageRoot, "_legacy-unmapped", "project-notebooks", project.Slug),
                knownNotebookIds: notebooks.Select(n => n.Id).ToHashSet());
        }

        var notebookContainer = Path.Combine(newProjectRoot, "notebooks");
        if (Directory.Exists(notebookContainer) && !Directory.EnumerateFileSystemEntries(notebookContainer).Any())
        {
            _logger.LogInformation("Removing empty legacy notebook container: {Path}", notebookContainer);
            if (apply)
            {
                Directory.Delete(notebookContainer, recursive: false);
            }
        }

        var oldCasProjectRoot = Path.Combine(_storageRoot, "projects", project.Id.ToString());
        var newCasProjectRoot = Path.Combine(_storageRoot, "projects", project.Slug);
        moveCount += MoveDirectoryIfNeeded(oldCasProjectRoot, newCasProjectRoot, apply, performedMoves);

        foreach (var notebook in notebooks)
        {
            var oldCasNotebookRoot = Path.Combine(newCasProjectRoot, "notebooks", notebook.Id.ToString());
            var newCasNotebookRoot = Path.Combine(newCasProjectRoot, notebook.Slug);
            moveCount += MoveDirectoryIfNeeded(oldCasNotebookRoot, newCasNotebookRoot, apply, performedMoves);
        }

        if (apply)
        {
            moveCount += ArchiveUnmappedNotebookDirectories(
                parentNotebookContainer: Path.Combine(newCasProjectRoot, "notebooks"),
                archiveParent: Path.Combine(_storageRoot, "_legacy-unmapped", "projects-cas-notebooks", project.Slug),
                knownNotebookIds: notebooks.Select(n => n.Id).ToHashSet());
        }

        var casNotebookContainer = Path.Combine(newCasProjectRoot, "notebooks");
        if (Directory.Exists(casNotebookContainer) && !Directory.EnumerateFileSystemEntries(casNotebookContainer).Any())
        {
            _logger.LogInformation("Removing empty legacy CAS notebook container: {Path}", casNotebookContainer);
            if (apply)
            {
                Directory.Delete(casNotebookContainer, recursive: false);
            }
        }

        return moveCount;
    }

    private int ArchiveUnmappedNotebookDirectories(
        string parentNotebookContainer,
        string archiveParent,
        HashSet<Guid> knownNotebookIds)
    {
        if (!Directory.Exists(parentNotebookContainer))
        {
            return 0;
        }

        var archived = 0;
        foreach (var dir in Directory.EnumerateDirectories(parentNotebookContainer))
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out var notebookId) || knownNotebookIds.Contains(notebookId))
            {
                continue;
            }

            Directory.CreateDirectory(archiveParent);
            var target = Path.Combine(archiveParent, name);
            if (Directory.Exists(target))
            {
                target = Path.Combine(archiveParent, $"{name}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            }

            _logger.LogInformation("Archiving unmatched legacy notebook directory: {Source} -> {Target}", dir, target);
            Directory.Move(dir, target);
            archived++;
        }

        return archived;
    }

    private int MoveDirectoryIfNeeded(
        string source,
        string target,
        bool apply,
        List<MoveOperation> performedMoves)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var sourceExists = Directory.Exists(source);
        var targetExists = Directory.Exists(target);

        if (!sourceExists)
        {
            return 0;
        }

        if (targetExists)
        {
            throw new InvalidOperationException(
                $"Cannot move '{source}' to '{target}' because target already exists.");
        }

        _logger.LogInformation("Moving directory: {Source} -> {Target}", source, target);
        if (apply)
        {
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(source, target);
            performedMoves.Add(new MoveOperation(target, source));
        }

        return 1;
    }

    private static void RollbackMoves(IEnumerable<MoveOperation> performedMoves)
    {
        foreach (var move in performedMoves.Reverse())
        {
            if (!Directory.Exists(move.Source) || Directory.Exists(move.Target))
            {
                continue;
            }

            Directory.Move(move.Source, move.Target);
        }
    }

    private static void EnsureNotebookAssociationMetadata(string notebookRoot, Guid projectId, Guid notebookId, bool apply)
    {
        var metadataDir = Path.Combine(notebookRoot, ".guideants");
        var metadataPath = Path.Combine(metadataDir, "notebook.json");

        if (!apply)
        {
            return;
        }

        Directory.CreateDirectory(metadataDir);
        if (OperatingSystem.IsWindows())
        {
            var attrs = File.GetAttributes(metadataDir);
            if (!attrs.HasFlag(FileAttributes.Hidden))
            {
                File.SetAttributes(metadataDir, attrs | FileAttributes.Hidden);
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            ProjectId = projectId,
            NotebookId = notebookId
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(metadataPath, payload);
    }

    private async Task<int> RewriteDatabasePathsAsync(
        ApplicationDbContext db,
        ProjectInfo project,
        IReadOnlyCollection<NotebookInfo> notebooks,
        CancellationToken cancellationToken)
    {
        var notebookSlugById = notebooks.ToDictionary(n => n.Id, n => n.Slug);
        var updateCount = 0;

        var contentFiles = await db.ContentFiles
            .Where(c => c.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var contentFile in contentFiles)
        {
            if (TryRewritePath(contentFile.Path, project, notebookSlugById, out var rewritten))
            {
                contentFile.Path = rewritten;
                updateCount++;
            }
        }

        var contentFileVersions = await db.ContentFileVersions
            .Where(v => v.ContentFile.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var version in contentFileVersions)
        {
            if (TryRewritePath(version.Path, project, notebookSlugById, out var rewrittenPath))
            {
                version.Path = rewrittenPath;
                updateCount++;
            }

            if (!string.IsNullOrWhiteSpace(version.StoragePath) &&
                TryRewritePath(version.StoragePath, project, notebookSlugById, out var rewrittenStoragePath))
            {
                version.StoragePath = rewrittenStoragePath;
                updateCount++;
            }
        }

        var contentShadows = await db.ContentFileMarkdownShadows
            .Where(s => s.OriginalVersion.ContentFile.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var shadow in contentShadows)
        {
            if (TryRewritePath(shadow.StoragePath, project, notebookSlugById, out var rewritten))
            {
                shadow.StoragePath = rewritten;
                updateCount++;
            }
        }

        var notebookShadows = await db.NotebookFileMarkdownShadows
            .Where(s => s.OriginalFile.Notebook.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var shadow in notebookShadows)
        {
            if (TryRewritePath(shadow.StoragePath, project, notebookSlugById, out var rewritten))
            {
                shadow.StoragePath = rewritten;
                updateCount++;
            }
        }

        var assistantShadows = await db.AssistantFileMarkdownShadows
            .Where(s => s.StoragePath.Contains(project.Id.ToString()))
            .ToListAsync(cancellationToken);
        foreach (var shadow in assistantShadows)
        {
            if (TryRewritePath(shadow.StoragePath, project, notebookSlugById, out var rewritten))
            {
                shadow.StoragePath = rewritten;
                updateCount++;
            }
        }

        return updateCount;
    }

    public bool TryRewritePath(
        string? originalPath,
        ProjectInfo project,
        IReadOnlyDictionary<Guid, string> notebookSlugById,
        out string rewrittenPath)
    {
        rewrittenPath = originalPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return false;
        }

        var oldProjectRoot = Path.Combine(_storageRoot, project.Id.ToString());
        var newProjectRoot = Path.Combine(_storageRoot, project.Slug);
        var oldCasRoot = Path.Combine(_storageRoot, "projects", project.Id.ToString());
        var newCasRoot = Path.Combine(_storageRoot, "projects", project.Slug);

        var rewritten = originalPath;

        foreach (var pair in BuildSeparatorVariants(oldProjectRoot, newProjectRoot))
        {
            rewritten = rewritten.Replace(pair.From, pair.To, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var pair in BuildSeparatorVariants(oldCasRoot, newCasRoot))
        {
            rewritten = rewritten.Replace(pair.From, pair.To, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var notebook in notebookSlugById)
        {
            var oldProjectNotebook = Path.Combine(newProjectRoot, "notebooks", notebook.Key.ToString());
            var newProjectNotebook = Path.Combine(newProjectRoot, notebook.Value);
            foreach (var pair in BuildSeparatorVariants(oldProjectNotebook, newProjectNotebook))
            {
                rewritten = rewritten.Replace(pair.From, pair.To, StringComparison.OrdinalIgnoreCase);
            }

            var oldCasNotebook = Path.Combine(newCasRoot, "notebooks", notebook.Key.ToString());
            var newCasNotebook = Path.Combine(newCasRoot, notebook.Value);
            foreach (var pair in BuildSeparatorVariants(oldCasNotebook, newCasNotebook))
            {
                rewritten = rewritten.Replace(pair.From, pair.To, StringComparison.OrdinalIgnoreCase);
            }
        }

        rewrittenPath = rewritten;
        return !string.Equals(originalPath, rewritten, StringComparison.Ordinal);
    }

    private static IEnumerable<ReplacementPair> BuildSeparatorVariants(string from, string to)
    {
        yield return new ReplacementPair(from, to);
        yield return new ReplacementPair(from.Replace('\\', '/'), to.Replace('\\', '/'));
        yield return new ReplacementPair(from.Replace('/', '\\'), to.Replace('/', '\\'));
    }

    private int ArchiveUnmappedLegacyDirectories(IReadOnlyCollection<ProjectInfo> projects)
    {
        var knownProjectIds = projects.Select(p => p.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archiveRoot = Path.Combine(_storageRoot, "_legacy-unmapped");
        var archived = 0;

        archived += ArchiveUnmappedGuidChildren(
            parent: _storageRoot,
            targetParent: Path.Combine(archiveRoot, "project-roots"),
            knownProjectIds: knownProjectIds,
            excludedDirectoryNames: ["projects", "assistants", "_legacy-unmapped"]);

        var casRoot = Path.Combine(_storageRoot, "projects");
        if (Directory.Exists(casRoot))
        {
            archived += ArchiveUnmappedGuidChildren(
                parent: casRoot,
                targetParent: Path.Combine(archiveRoot, "projects-cas"),
                knownProjectIds: knownProjectIds,
                excludedDirectoryNames: []);
        }

        return archived;
    }

    private int ArchiveUnmappedGuidChildren(
        string parent,
        string targetParent,
        HashSet<string> knownProjectIds,
        IReadOnlyCollection<string> excludedDirectoryNames)
    {
        if (!Directory.Exists(parent))
        {
            return 0;
        }

        var archived = 0;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            var name = Path.GetFileName(dir);
            if (excludedDirectoryNames.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!Guid.TryParse(name, out _) || knownProjectIds.Contains(name))
            {
                continue;
            }

            Directory.CreateDirectory(targetParent);
            var target = Path.Combine(targetParent, name);
            if (Directory.Exists(target))
            {
                target = Path.Combine(targetParent, $"{name}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            }

            _logger.LogInformation("Archiving unmatched legacy directory: {Source} -> {Target}", dir, target);
            Directory.Move(dir, target);
            archived++;
        }

        return archived;
    }

    private async Task<ValidationResult> ValidateNoLegacyReferencesAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<ProjectInfo> projects,
        CancellationToken cancellationToken)
    {
        var legacyFsReferences = 0;
        foreach (var project in projects)
        {
            if (Directory.Exists(Path.Combine(_storageRoot, project.Id.ToString())))
            {
                legacyFsReferences++;
            }

            if (Directory.Exists(Path.Combine(_storageRoot, "projects", project.Id.ToString())))
            {
                legacyFsReferences++;
            }
        }

        var legacyDbReferences = 0;
        foreach (var project in projects)
        {
            var oldProjectRoot = Path.Combine(_storageRoot, project.Id.ToString());
            var oldCasRoot = Path.Combine(_storageRoot, "projects", project.Id.ToString());
            var oldProjectRootSlash = oldProjectRoot.Replace('\\', '/');
            var oldCasRootSlash = oldCasRoot.Replace('\\', '/');

            legacyDbReferences += await db.ContentFiles.CountAsync(c =>
                c.ProjectId == project.Id &&
                (c.Path.StartsWith(oldProjectRoot) || c.Path.StartsWith(oldProjectRootSlash)), cancellationToken);

            legacyDbReferences += await db.ContentFileVersions.CountAsync(v =>
                v.ContentFile.ProjectId == project.Id &&
                (
                    v.Path.StartsWith(oldProjectRoot) ||
                    v.Path.StartsWith(oldProjectRootSlash) ||
                    (v.StoragePath != null && (v.StoragePath.StartsWith(oldProjectRoot) || v.StoragePath.StartsWith(oldProjectRootSlash))) ||
                    v.Path.StartsWith(oldCasRoot) ||
                    v.Path.StartsWith(oldCasRootSlash) ||
                    (v.StoragePath != null && (v.StoragePath.StartsWith(oldCasRoot) || v.StoragePath.StartsWith(oldCasRootSlash)))
                ), cancellationToken);

            legacyDbReferences += await db.ContentFileMarkdownShadows.CountAsync(s =>
                s.OriginalVersion.ContentFile.ProjectId == project.Id &&
                (
                    s.StoragePath.StartsWith(oldCasRoot) ||
                    s.StoragePath.StartsWith(oldCasRootSlash) ||
                    s.StoragePath.StartsWith(oldProjectRoot) ||
                    s.StoragePath.StartsWith(oldProjectRootSlash)
                ), cancellationToken);

            legacyDbReferences += await db.NotebookFileMarkdownShadows.CountAsync(s =>
                s.OriginalFile.Notebook.ProjectId == project.Id &&
                (
                    s.StoragePath.StartsWith(oldCasRoot) ||
                    s.StoragePath.StartsWith(oldCasRootSlash) ||
                    s.StoragePath.StartsWith(oldProjectRoot) ||
                    s.StoragePath.StartsWith(oldProjectRootSlash)
                ), cancellationToken);
        }

        legacyFsReferences += CountActiveLegacyGuidDirectories(_storageRoot, excluded: ["projects", "assistants", "_legacy-unmapped"]);
        legacyFsReferences += CountActiveLegacyGuidDirectories(Path.Combine(_storageRoot, "projects"), excluded: []);

        return new ValidationResult(legacyDbReferences, legacyFsReferences);
    }

    private static int CountActiveLegacyGuidDirectories(string parent, IReadOnlyCollection<string> excluded)
    {
        if (!Directory.Exists(parent))
        {
            return 0;
        }

        return Directory.EnumerateDirectories(parent)
            .Select(Path.GetFileName)
            .Count(name =>
                name != null &&
                !excluded.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)) &&
                Guid.TryParse(name, out _));
    }

    public sealed record ProjectInfo(Guid Id, string Slug);
    public sealed record NotebookInfo(Guid Id, Guid ProjectId, string Slug);
    private sealed record MoveOperation(string Source, string Target);
    private sealed record ReplacementPair(string From, string To);
    private sealed record ValidationResult(int LegacyDbReferences, int LegacyFilesystemReferences);
}
