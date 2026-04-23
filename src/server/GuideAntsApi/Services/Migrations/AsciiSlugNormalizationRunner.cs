using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Migrations;

public sealed class AsciiSlugNormalizationRunner
{
    private const string TempPrefix = "__slugnorm__";
    private const string TempNotebookPrefix = "__slugnorm_nb__";

    private readonly string _connectionString;
    private readonly string _storageRoot;
    private readonly ILogger _logger;

    public AsciiSlugNormalizationRunner(string connectionString, string storageRoot, ILogger logger)
    {
        _connectionString = connectionString;
        _storageRoot = storageRoot;
        _logger = logger;
    }

    public async Task<int> RunAsync(bool apply, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ASCII slug normalization started. Apply mode: {Apply}", apply);
        _logger.LogInformation("Storage root: {StorageRoot}", _storageRoot);

        if (!Directory.Exists(_storageRoot))
        {
            throw new DirectoryNotFoundException($"Storage root not found: {_storageRoot}");
        }

        await using var db = CreateDbContext();

        var projectRows = await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new ProjectRow(p.Id, p.Title, p.Slug))
            .ToListAsync(cancellationToken);

        var notebookRows = await db.Notebooks
            .AsNoTracking()
            .OrderBy(n => n.ProjectId)
            .ThenBy(n => n.Id)
            .Select(n => new NotebookRow(n.Id, n.ProjectId, n.Title, n.Slug))
            .ToListAsync(cancellationToken);

        var plan = BuildPlan(projectRows, notebookRows);
        _logger.LogInformation(
            "Normalization plan generated. Project slug changes: {ProjectChanges}, Notebook slug changes: {NotebookChanges}",
            plan.ProjectChanges.Count,
            plan.NotebookChanges.Count);

        if (!apply)
        {
            foreach (var change in plan.ProjectChanges.Take(10))
            {
                _logger.LogInformation("Project slug change: {Id} {Old} -> {New}", change.ProjectId, change.OldSlug, change.NewSlug);
            }

            foreach (var change in plan.NotebookChanges.Take(10))
            {
                _logger.LogInformation("Notebook slug change: {Id} {Old} -> {New}", change.NotebookId, change.OldSlug, change.NewSlug);
            }

            return 0;
        }

        if (plan.ProjectChanges.Count == 0 && plan.NotebookChanges.Count == 0)
        {
            _logger.LogInformation("No slug changes required.");
            return 0;
        }

        var performedMoves = new List<MoveOperation>();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            ExecuteFilesystemPlan(plan, performedMoves);
            await ApplyDatabaseUpdatesAsync(db, plan, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            RollbackMoves(performedMoves);
            throw;
        }

        _logger.LogInformation(
            "ASCII slug normalization complete. Project changes: {ProjectChanges}, Notebook changes: {NotebookChanges}",
            plan.ProjectChanges.Count,
            plan.NotebookChanges.Count);
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

    private static NormalizationPlan BuildPlan(
        IReadOnlyCollection<ProjectRow> projects,
        IReadOnlyCollection<NotebookRow> notebooks)
    {
        var targetProjectSlugById = new Dictionary<Guid, string>();
        var projectChanges = new List<ProjectSlugChange>();
        var usedProjectSlugs = new HashSet<string>(StringComparer.Ordinal);
        var projectsNeedingNormalization = new List<ProjectRow>();

        foreach (var project in projects)
        {
            if (SlugGenerator.IsFilesystemSafeSlug(project.Slug) && usedProjectSlugs.Add(project.Slug))
            {
                targetProjectSlugById[project.Id] = project.Slug;
                continue;
            }

            projectsNeedingNormalization.Add(project);
        }

        foreach (var project in projectsNeedingNormalization)
        {
            var baseSlug = SlugGenerator.Generate(project.Title);
            var slug = baseSlug;
            var suffix = 2;
            while (!usedProjectSlugs.Add(slug))
            {
                slug = SlugGenerator.AddNumericSuffix(baseSlug, suffix++);
            }

            targetProjectSlugById[project.Id] = slug;
            projectChanges.Add(new ProjectSlugChange(project.Id, project.Slug, slug));
        }

        var notebookChanges = new List<NotebookSlugChange>();
        var notebooksByProject = notebooks.GroupBy(n => n.ProjectId);
        foreach (var group in notebooksByProject)
        {
            var usedNotebookSlugs = new HashSet<string>(StringComparer.Ordinal);
            var notebooksNeedingNormalization = new List<NotebookRow>();
            foreach (var notebook in group.OrderBy(n => n.Id))
            {
                if (SlugGenerator.IsFilesystemSafeSlug(notebook.Slug) && usedNotebookSlugs.Add(notebook.Slug))
                {
                    continue;
                }

                notebooksNeedingNormalization.Add(notebook);
            }

            foreach (var notebook in notebooksNeedingNormalization)
            {
                var baseSlug = SlugGenerator.Generate(notebook.Title);
                var slug = baseSlug;
                var suffix = 2;
                while (!usedNotebookSlugs.Add(slug))
                {
                    slug = SlugGenerator.AddNumericSuffix(baseSlug, suffix++);
                }

                notebookChanges.Add(new NotebookSlugChange(notebook.Id, notebook.ProjectId, notebook.Slug, slug));
            }
        }

        return new NormalizationPlan(projectChanges, notebookChanges, targetProjectSlugById);
    }

    private void ExecuteFilesystemPlan(NormalizationPlan plan, List<MoveOperation> performedMoves)
    {
        var projectChangeById = plan.ProjectChanges.ToDictionary(x => x.ProjectId);

        PhaseMoveProjectRoots(
            plan.ProjectChanges,
            _storageRoot,
            performedMoves);

        var casRoot = Path.Combine(_storageRoot, "projects");
        PhaseMoveProjectRoots(
            plan.ProjectChanges,
            casRoot,
            performedMoves);

        var notebookChangesByProject = plan.NotebookChanges
            .GroupBy(n => n.ProjectId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var projectId in notebookChangesByProject.Keys)
        {
            if (!plan.TargetProjectSlugs.TryGetValue(projectId, out var targetProjectSlug))
            {
                continue;
            }

            var projectRoot = Path.Combine(_storageRoot, targetProjectSlug);
            PhaseMoveNotebookRoots(notebookChangesByProject[projectId], projectRoot, performedMoves);

            var projectCasRoot = Path.Combine(_storageRoot, "projects", targetProjectSlug);
            PhaseMoveNotebookRoots(notebookChangesByProject[projectId], projectCasRoot, performedMoves);
        }
    }

    private void PhaseMoveProjectRoots(
        IReadOnlyCollection<ProjectSlugChange> projectChanges,
        string parent,
        List<MoveOperation> performedMoves)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        var stagedMoves = new List<(string Temp, string Target)>();
        foreach (var change in projectChanges)
        {
            var source = Path.Combine(parent, change.OldSlug);
            var target = Path.Combine(parent, change.NewSlug);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source))
            {
                continue;
            }

            if (Directory.Exists(target))
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                {
                    _logger.LogInformation("Source already empty and target exists, skipping project move shell: {Source} -> {Target}", source, target);
                    continue;
                }

                throw new InvalidOperationException($"Target path already exists and source is not empty: {target}");
            }

            var temp = Path.Combine(parent, $"{TempPrefix}{change.ProjectId:N}");
            if (Directory.Exists(temp))
            {
                throw new InvalidOperationException($"Temporary path already exists: {temp}");
            }

            _logger.LogInformation("Staging project root move: {Source} -> {Temp}", source, temp);
            Directory.Move(source, temp);
            stagedMoves.Add((temp, target));
            performedMoves.Add(new MoveOperation(temp, source));
        }

        foreach (var move in stagedMoves)
        {
            if (Directory.Exists(move.Target))
            {
                throw new InvalidOperationException($"Target path already exists: {move.Target}");
            }

            _logger.LogInformation("Applying project root move: {Temp} -> {Target}", move.Temp, move.Target);
            Directory.Move(move.Temp, move.Target);
            performedMoves.Add(new MoveOperation(move.Target, move.Temp));
        }
    }

    private void PhaseMoveNotebookRoots(
        IReadOnlyCollection<NotebookSlugChange> notebookChanges,
        string projectRoot,
        List<MoveOperation> performedMoves)
    {
        if (!Directory.Exists(projectRoot))
        {
            return;
        }

        var stagedMoves = new List<(string Temp, string Target)>();
        foreach (var change in notebookChanges)
        {
            var source = Path.Combine(projectRoot, change.OldSlug);
            var target = Path.Combine(projectRoot, change.NewSlug);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source))
            {
                continue;
            }

            if (Directory.Exists(target))
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                {
                    _logger.LogInformation("Source already empty and target exists, skipping notebook move shell: {Source} -> {Target}", source, target);
                    continue;
                }

                throw new InvalidOperationException($"Target notebook path already exists and source is not empty: {target}");
            }

            var temp = Path.Combine(projectRoot, $"{TempNotebookPrefix}{change.NotebookId:N}");
            if (Directory.Exists(temp))
            {
                throw new InvalidOperationException($"Temporary notebook path already exists: {temp}");
            }

            _logger.LogInformation("Staging notebook root move: {Source} -> {Temp}", source, temp);
            Directory.Move(source, temp);
            stagedMoves.Add((temp, target));
            performedMoves.Add(new MoveOperation(temp, source));
        }

        foreach (var move in stagedMoves)
        {
            if (Directory.Exists(move.Target))
            {
                throw new InvalidOperationException($"Target notebook path already exists: {move.Target}");
            }

            _logger.LogInformation("Applying notebook root move: {Temp} -> {Target}", move.Temp, move.Target);
            Directory.Move(move.Temp, move.Target);
            performedMoves.Add(new MoveOperation(move.Target, move.Temp));
        }
    }

    private async Task ApplyDatabaseUpdatesAsync(
        ApplicationDbContext db,
        NormalizationPlan plan,
        CancellationToken cancellationToken)
    {
        var projectIds = plan.TargetProjectSlugs.Keys.ToHashSet();
        var replacementPairs = BuildReplacementPairs(plan);

        var dbProjects = await db.Projects
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        foreach (var change in plan.ProjectChanges)
        {
            dbProjects[change.ProjectId].Slug = change.NewSlug;
        }

        var dbNotebooks = await db.Notebooks
            .Where(n => projectIds.Contains(n.ProjectId))
            .ToDictionaryAsync(n => n.Id, cancellationToken);
        foreach (var change in plan.NotebookChanges)
        {
            dbNotebooks[change.NotebookId].Slug = change.NewSlug;
        }

        var contentFiles = await db.ContentFiles
            .Where(c => projectIds.Contains(c.ProjectId))
            .ToListAsync(cancellationToken);
        foreach (var row in contentFiles)
        {
            row.Path = RewritePath(row.Path, replacementPairs);
        }

        var contentFileVersions = await db.ContentFileVersions
            .Where(v => projectIds.Contains(v.ContentFile.ProjectId))
            .ToListAsync(cancellationToken);
        foreach (var row in contentFileVersions)
        {
            row.Path = RewritePath(row.Path, replacementPairs);
            if (!string.IsNullOrWhiteSpace(row.StoragePath))
            {
                row.StoragePath = RewritePath(row.StoragePath, replacementPairs);
            }
        }

        var contentShadows = await db.ContentFileMarkdownShadows
            .Where(s => projectIds.Contains(s.OriginalVersion.ContentFile.ProjectId))
            .ToListAsync(cancellationToken);
        foreach (var row in contentShadows)
        {
            row.StoragePath = RewritePath(row.StoragePath, replacementPairs);
        }

        var notebookShadows = await db.NotebookFileMarkdownShadows
            .Where(s => projectIds.Contains(s.OriginalFile.Notebook.ProjectId))
            .ToListAsync(cancellationToken);
        foreach (var row in notebookShadows)
        {
            row.StoragePath = RewritePath(row.StoragePath, replacementPairs);
        }

        var assistantShadows = await db.AssistantFileMarkdownShadows
            .ToListAsync(cancellationToken);
        foreach (var row in assistantShadows)
        {
            row.StoragePath = RewritePath(row.StoragePath, replacementPairs);
        }
    }

    private List<ReplacementPair> BuildReplacementPairs(NormalizationPlan plan)
    {
        var pairs = new List<ReplacementPair>();
        foreach (var project in plan.ProjectChanges)
        {
            var oldRoot = Path.Combine(_storageRoot, project.OldSlug);
            var newRoot = Path.Combine(_storageRoot, project.NewSlug);
            AddReplacementVariants(pairs, oldRoot, newRoot);

            var oldCasRoot = Path.Combine(_storageRoot, "projects", project.OldSlug);
            var newCasRoot = Path.Combine(_storageRoot, "projects", project.NewSlug);
            AddReplacementVariants(pairs, oldCasRoot, newCasRoot);
        }

        var projectOldById = plan.ProjectChanges.ToDictionary(x => x.ProjectId, x => x.OldSlug);
        var projectNewById = plan.TargetProjectSlugs;
        foreach (var notebook in plan.NotebookChanges)
        {
            if (!projectNewById.TryGetValue(notebook.ProjectId, out var newProjectSlug))
            {
                continue;
            }

            var oldProjectSlug = projectOldById.TryGetValue(notebook.ProjectId, out var changedOld)
                ? changedOld
                : newProjectSlug;

            var oldRoot = Path.Combine(_storageRoot, oldProjectSlug, notebook.OldSlug);
            var newRoot = Path.Combine(_storageRoot, newProjectSlug, notebook.NewSlug);
            AddReplacementVariants(pairs, oldRoot, newRoot);

            var oldCasRoot = Path.Combine(_storageRoot, "projects", oldProjectSlug, notebook.OldSlug);
            var newCasRoot = Path.Combine(_storageRoot, "projects", newProjectSlug, notebook.NewSlug);
            AddReplacementVariants(pairs, oldCasRoot, newCasRoot);
        }

        return pairs;
    }

    private static void AddReplacementVariants(List<ReplacementPair> pairs, string from, string to)
    {
        pairs.Add(new ReplacementPair(from, to));

        // Container absolute path variants commonly stored in DB.
        const string containerRoot = "/app/ContentFiles";
        var normalizedFrom = from.Replace('\\', '/');
        var normalizedTo = to.Replace('\\', '/');
        var marker = "/content-files";
        var markerIndex = normalizedFrom.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var tailFrom = normalizedFrom[(markerIndex + marker.Length)..];
            var tailTo = normalizedTo[(markerIndex + marker.Length)..];
            pairs.Add(new ReplacementPair($"{containerRoot}{tailFrom}", $"{containerRoot}{tailTo}"));
        }

        // Relative root variants (projects/...).
        var rootToken = "/projects/";
        var projectIndexFrom = normalizedFrom.IndexOf(rootToken, StringComparison.OrdinalIgnoreCase);
        var projectIndexTo = normalizedTo.IndexOf(rootToken, StringComparison.OrdinalIgnoreCase);
        if (projectIndexFrom >= 0 && projectIndexTo >= 0)
        {
            pairs.Add(new ReplacementPair(
                normalizedFrom[(projectIndexFrom + 1)..],
                normalizedTo[(projectIndexTo + 1)..]));
        }
    }

    private static string RewritePath(string value, IReadOnlyCollection<ReplacementPair> replacementPairs)
    {
        var rewritten = value;
        foreach (var pair in replacementPairs)
        {
            foreach (var variant in BuildSeparatorVariants(pair.From, pair.To))
            {
                rewritten = rewritten.Replace(variant.From, variant.To, StringComparison.OrdinalIgnoreCase);
            }
        }

        return rewritten;
    }

    private static IEnumerable<ReplacementPair> BuildSeparatorVariants(string from, string to)
    {
        yield return new ReplacementPair(from, to);
        yield return new ReplacementPair(from.Replace('\\', '/'), to.Replace('\\', '/'));
        yield return new ReplacementPair(from.Replace('/', '\\'), to.Replace('/', '\\'));
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

    private sealed record ProjectRow(Guid Id, string Title, string Slug);
    private sealed record NotebookRow(Guid Id, Guid ProjectId, string Title, string Slug);
    private sealed record ProjectSlugChange(Guid ProjectId, string OldSlug, string NewSlug);
    private sealed record NotebookSlugChange(Guid NotebookId, Guid ProjectId, string OldSlug, string NewSlug);
    private sealed record NormalizationPlan(
        List<ProjectSlugChange> ProjectChanges,
        List<NotebookSlugChange> NotebookChanges,
        Dictionary<Guid, string> TargetProjectSlugs);
    private sealed record MoveOperation(string Source, string Target);
    private sealed record ReplacementPair(string From, string To);
}
