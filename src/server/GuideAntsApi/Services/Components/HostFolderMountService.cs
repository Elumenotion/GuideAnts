using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Components;

public sealed class HostFolderMountService : IHostFolderMountService
{
    private const string UnlinkFailureRemediation =
        "Failed to remove one or more notebook symlinks. Resolve the reported link errors and retry remove, or run reconcile after fixing the notebook root.";

    private static readonly string[] AffectedMountServices =
    [
        "guideants-webapi-ui",
        "guideants-ai",
        "plantuml"
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStoragePathResolver _pathResolver;
    private readonly string _hostMountsRoot;
    private readonly HostFolderMountLeafValidator _leafValidator = new();
    private readonly NotebookMountsRegistryWriter _registryWriter = new();

    public HostFolderMountService(
        IServiceScopeFactory scopeFactory,
        IStoragePathResolver pathResolver,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _pathResolver = pathResolver;
        _hostMountsRoot = configuration["FileStorage:HostMountsRoot"]
            ?? HostFolderMountKeyDeriver.ContainerMountRoot;
    }

    public async Task<HostFolderMountCreateResult> CreateMountAsync(
        Guid projectId,
        HostFolderMountCreateRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SourceKind != SourceKind.LocalPath)
        {
            throw new NotSupportedException("Only LocalPath sources are supported in the first implementation.");
        }

        if (string.IsNullOrWhiteSpace(request.HostPath))
        {
            throw new ArgumentException("Host path is required.", nameof(request));
        }

        if (!HostMountHostPath.IsAbsoluteHostPath(request.HostPath))
        {
            throw new ArgumentException("Host path must be an absolute path.", nameof(request));
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectExists = await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
        {
            throw new KeyNotFoundException($"Project {projectId} was not found.");
        }

        if (request.Scope == HostFolderMountScope.Notebook)
        {
            if (request.NotebookId is not Guid notebookId)
            {
                throw new ArgumentException("Notebook scope requires notebookId.", nameof(request));
            }

            var notebookBelongs = await db.Notebooks.AsNoTracking()
                .AnyAsync(n => n.Id == notebookId && n.ProjectId == projectId, cancellationToken);
            if (!notebookBelongs)
            {
                throw new KeyNotFoundException($"Notebook {notebookId} was not found in project {projectId}.");
            }
        }
        else if (request.NotebookId is not null)
        {
            throw new ArgumentException("Project scope must not include notebookId.", nameof(request));
        }

        var leafName = string.IsNullOrWhiteSpace(request.LeafName)
            ? HostFolderMountKeyDeriver.DeriveDefaultLeafName(request.HostPath)
            : request.LeafName.Trim();

        var validation = await ValidateLeafNameAsync(
            projectId,
            request.Scope,
            request.NotebookId,
            leafName,
            excludeMountId: null,
            cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage ?? "Leaf name validation failed.");
        }

        var projectNotebookIds = await db.Notebooks.AsNoTracking()
            .Where(n => n.ProjectId == projectId)
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);

        var targetNotebookIds = HostFolderMountLeafValidator.ResolveTargetNotebookIds(
            request.Scope,
            request.NotebookId,
            projectNotebookIds);

        var mountId = Guid.NewGuid();
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
        var containerSourcePath = HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey);
        var now = DateTime.UtcNow;

        var mount = new HostFolderMount
        {
            Id = mountId,
            ProjectId = projectId,
            NotebookId = request.Scope == HostFolderMountScope.Notebook ? request.NotebookId : null,
            Scope = request.Scope,
            SourceKind = request.SourceKind,
            DisplayName = HostFolderMountKeyDeriver.DeriveDisplayName(request.HostPath),
            LeafName = leafName,
            MountKey = mountKey,
            SourceSpec = request.HostPath,
            ContainerSourcePath = containerSourcePath,
            Status = HostFolderMountStatus.PendingRestart,
            CreatedByUserId = createdByUserId,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        foreach (var notebookId in targetNotebookIds)
        {
            var notebookRoot = _pathResolver.GetNotebookRootPath(projectId, notebookId);
            var linkPhysicalPath = Path.Combine(notebookRoot, leafName);

            mount.Links.Add(new HostFolderMountLink
            {
                NotebookId = notebookId,
                LinkRelativePath = leafName,
                LinkPhysicalPath = linkPhysicalPath,
                Status = HostFolderMountLinkStatus.PendingRestart
            });
        }

        db.HostFolderMounts.Add(mount);
        await db.SaveChangesAsync(cancellationToken);

        var applyCommand = BuildApplyCommandText(mountId, projectId, request.HostPath);

        return new HostFolderMountCreateResult(
            mountId,
            mount.Status,
            leafName,
            mountKey,
            containerSourcePath,
            applyCommand);
    }

    public async Task<IReadOnlyList<HostFolderMount>> ListMountsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.HostFolderMounts.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderByDescending(m => m.CreatedUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<HostFolderMount?> GetMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.HostFolderMounts.AsNoTracking()
            .Include(m => m.Links)
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == mountId, cancellationToken);
    }

    public async Task<HostFolderMountApplyCommandResult> GetApplyCommandAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            throw new KeyNotFoundException($"Host folder mount {mountId} was not found in project {projectId}.");
        }

        if (mount.SourceKind != SourceKind.LocalPath)
        {
            throw new NotSupportedException("Only LocalPath sources are supported in the first implementation.");
        }

        var applyCommand = BuildApplyCommandText(mount.Id, mount.ProjectId, mount.SourceSpec);

        return new HostFolderMountApplyCommandResult(
            mount.Id,
            mount.Status,
            mount.LeafName,
            mount.ContainerSourcePath,
            applyCommand);
    }

    public async Task<HostFolderMountRemoveCommandResult> BeginRemoveMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts
            .Include(m => m.Links)
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            throw new KeyNotFoundException($"Host folder mount {mountId} was not found in project {projectId}.");
        }

        if (mount.Status == HostFolderMountStatus.Removed)
        {
            throw new InvalidOperationException("Mount has already been removed.");
        }

        mount.Status = HostFolderMountStatus.PendingRemoval;
        mount.ErrorMessage = null;
        mount.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var unlinkFailed = await TryUnlinkAllMountLinksAsync(db, mount, cancellationToken);
        if (unlinkFailed)
        {
            mount.Status = HostFolderMountStatus.Error;
            mount.ErrorMessage = UnlinkFailureRemediation;
            mount.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(UnlinkFailureRemediation);
        }

        var removeCommand = BuildRemoveCommandText(mount.Id);

        return new HostFolderMountRemoveCommandResult(
            mount.Id,
            mount.Status,
            removeCommand);
    }

    public async Task<bool> DeleteMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            return false;
        }

        db.HostFolderMounts.Remove(mount);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<HostFolderMountReconcileResult> ReconcileMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts
            .Include(m => m.Links)
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            throw new KeyNotFoundException($"Host folder mount {mountId} was not found in project {projectId}.");
        }

        await ReconcileMountsInternalAsync(
            db,
            projectId: projectId,
            notebookId: null,
            mountId: mountId,
            cancellationToken);

        await db.Entry(mount).ReloadAsync(cancellationToken);
        await db.Entry(mount).Collection(m => m.Links).LoadAsync(cancellationToken);

        return new HostFolderMountReconcileResult(
            mount.Id,
            mount.Status,
            BuildReconcileMessage(mount));
    }

    public async Task<HostFolderMountComposeOverridePlanScriptResponse?> GetComposeOverridePlanForScriptAsync(
        Guid mountId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            return null;
        }

        var hostPath = mount.SourceKind switch
        {
            SourceKind.LocalPath => HostFolderMountCommandTextBuilder.FormatHostPathForCompose(mount.SourceSpec),
            SourceKind.Smb => mount.NetworkDevice ?? mount.SourceSpec,
            _ => throw new NotSupportedException($"Unsupported source kind '{mount.SourceKind}'.")
        };

        return new HostFolderMountComposeOverridePlanScriptResponse(
            mount.Id,
            mount.ProjectId,
            mount.MountKey,
            mount.SourceKind,
            hostPath);
    }

    public async Task<HostFolderMountValidationResult> ValidateLeafNameAsync(
        Guid projectId,
        HostFolderMountScope scope,
        Guid? notebookId,
        string leafName,
        Guid? excludeMountId = null,
        CancellationToken cancellationToken = default)
    {
        using var scopeFactory = _scopeFactory.CreateScope();
        var db = scopeFactory.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (scope == HostFolderMountScope.Notebook && notebookId is not Guid singleNotebookId)
        {
            return HostFolderMountValidationResult.Fail(
                "notebook_required",
                "Notebook scope requires a notebook id.");
        }

        if (scope == HostFolderMountScope.Notebook)
        {
            var notebookBelongs = await db.Notebooks.AsNoTracking()
                .AnyAsync(n => n.Id == notebookId!.Value && n.ProjectId == projectId, cancellationToken);
            if (!notebookBelongs)
            {
                return HostFolderMountValidationResult.Fail(
                    "notebook_not_found",
                    $"Notebook {notebookId} was not found in project {projectId}.");
            }
        }

        var projectNotebookIds = await db.Notebooks.AsNoTracking()
            .Where(n => n.ProjectId == projectId)
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);

        var targetNotebookIds = HostFolderMountLeafValidator.ResolveTargetNotebookIds(
            scope,
            notebookId,
            projectNotebookIds);

        return await _leafValidator.ValidateCollisionsAsync(
            db,
            _pathResolver,
            projectId,
            targetNotebookIds,
            leafName,
            excludeMountId,
            cancellationToken);
    }

    public HostFolderMountComposeOverridePlan BuildComposeOverridePlan(HostFolderMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);

        var affectedServices = AffectedMountServices;
        var containerTarget = mount.ContainerSourcePath;

        if (mount.SourceKind == SourceKind.LocalPath)
        {
            var composeHostPath = HostFolderMountCommandTextBuilder.FormatHostPathForCompose(mount.SourceSpec);
            var bindVolumes = affectedServices
                .Select(service => new HostFolderMountComposeBindVolume(
                    service,
                    composeHostPath,
                    containerTarget,
                    ReadOnly: false))
                .ToList();

            return new HostFolderMountComposeOverridePlan(
                mount.Id,
                mount.ProjectId,
                mount.MountKey,
                mount.SourceKind,
                containerTarget,
                affectedServices,
                bindVolumes,
                SmbVolume: null);
        }

        if (mount.SourceKind == SourceKind.Smb)
        {
            var volumeName = SanitizeComposeVolumeName(mount.MountKey);
            var driverOptions = BuildSmbDriverOptions(mount);
            var smbVolume = new HostFolderMountComposeSmbVolume(
                volumeName,
                mount.NetworkDevice ?? string.Empty,
                driverOptions,
                mount.CredentialRef);

            return new HostFolderMountComposeOverridePlan(
                mount.Id,
                mount.ProjectId,
                mount.MountKey,
                mount.SourceKind,
                containerTarget,
                affectedServices,
                BindVolumes: null,
                smbVolume);
        }

        throw new NotSupportedException($"Unsupported source kind '{mount.SourceKind}'.");
    }

    public string BuildApplyCommandText(Guid mountId, Guid projectId, string hostPath) =>
        HostFolderMountCommandTextBuilder.BuildApplyCommand(mountId, projectId, hostPath);

    public string BuildRemoveCommandText(Guid mountId) =>
        HostFolderMountCommandTextBuilder.BuildRemoveCommand(mountId);

    public async Task CreateSymlinksForMountAsync(Guid mountId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts
            .Include(m => m.Links)
            .FirstOrDefaultAsync(m => m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            throw new KeyNotFoundException($"Host folder mount {mountId} was not found.");
        }

        Exception? firstFailure = null;

        foreach (var link in mount.Links)
        {
            link.Status = HostFolderMountLinkStatus.PendingLink;
            link.LastCheckedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var link in mount.Links)
        {
            try
            {
                CreateDirectorySymlinkForLink(mount, link);
                link.Status = HostFolderMountLinkStatus.Linked;
                link.LastLinkedUtc = DateTime.UtcNow;
                link.ErrorMessage = null;
            }
            catch (Exception ex)
            {
                link.Status = HostFolderMountLinkStatus.LinkError;
                link.ErrorMessage = ex.Message;
                link.LastCheckedUtc = DateTime.UtcNow;
                firstFailure ??= ex;
            }
        }

        mount.UpdatedUtc = DateTime.UtcNow;
        if (firstFailure == null && mount.Links.Count > 0 && mount.Links.All(l => l.Status == HostFolderMountLinkStatus.Linked))
        {
            mount.Status = HostFolderMountStatus.Active;
        }
        else if (firstFailure != null)
        {
            mount.Status = HostFolderMountStatus.Error;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (firstFailure != null)
        {
            throw new InvalidOperationException(
                $"Failed to create one or more mount symlinks for mount {mountId}.",
                firstFailure);
        }

        await WriteMountsRegistriesForNotebooksAsync(
            db,
            mount.Links.Select(l => l.NotebookId).Distinct().ToList(),
            cancellationToken);
    }

    public async Task RemoveSymlinksForMountAsync(Guid mountId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var mount = await db.HostFolderMounts
            .Include(m => m.Links)
            .FirstOrDefaultAsync(m => m.Id == mountId, cancellationToken);
        if (mount == null)
        {
            throw new KeyNotFoundException($"Host folder mount {mountId} was not found.");
        }

        var unlinkFailed = await TryUnlinkAllMountLinksAsync(db, mount, cancellationToken);
        if (unlinkFailed)
        {
            if (mount.Status is HostFolderMountStatus.PendingRemoval or HostFolderMountStatus.Active)
            {
                mount.Status = HostFolderMountStatus.Error;
            }

            mount.ErrorMessage = UnlinkFailureRemediation;
            mount.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(UnlinkFailureRemediation);
        }
    }

    public async Task UpdateMountsRegistryAsync(Guid mountId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebookIds = await db.HostFolderMountLinks
            .AsNoTracking()
            .Where(l => l.HostFolderMountId == mountId)
            .Select(l => l.NotebookId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (notebookIds.Count == 0)
        {
            return;
        }

        await WriteMountsRegistriesForNotebooksAsync(db, notebookIds, cancellationToken);
    }

    public async Task ReconcileProjectMountsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectExists = await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
        {
            throw new KeyNotFoundException($"Project {projectId} was not found.");
        }

        await ReconcileMountsInternalAsync(
            db,
            projectId: projectId,
            notebookId: null,
            mountId: null,
            cancellationToken);
    }

    public async Task ReconcileNotebookMountsAsync(
        Guid projectId,
        Guid notebookId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebookExists = await db.Notebooks.AsNoTracking()
            .AnyAsync(n => n.Id == notebookId && n.ProjectId == projectId, cancellationToken);
        if (!notebookExists)
        {
            throw new KeyNotFoundException($"Notebook {notebookId} was not found in project {projectId}.");
        }

        await ReconcileMountsInternalAsync(
            db,
            projectId: projectId,
            notebookId: notebookId,
            mountId: null,
            cancellationToken);
    }

    public async Task ReconcileAllMountsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await ReconcileMountsInternalAsync(
            db,
            projectId: null,
            notebookId: null,
            mountId: null,
            cancellationToken);
    }

    public async Task ApplyProjectScopedMappingsToNewNotebookAsync(
        Guid projectId,
        Guid notebookId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebookExists = await db.Notebooks.AsNoTracking()
            .AnyAsync(n => n.Id == notebookId && n.ProjectId == projectId, cancellationToken);
        if (!notebookExists)
        {
            throw new KeyNotFoundException($"Notebook {notebookId} was not found in project {projectId}.");
        }

        var activeProjectMounts = await db.HostFolderMounts
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId
                        && m.Scope == HostFolderMountScope.Project
                        && m.Status != HostFolderMountStatus.Removed
                        && m.Status != HostFolderMountStatus.PendingRemoval)
            .Select(m => new { m.Id, m.LeafName })
            .ToListAsync(cancellationToken);

        if (activeProjectMounts.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var notebookRoot = _pathResolver.GetNotebookRootPath(projectId, notebookId);
        var existingMountIds = await db.HostFolderMountLinks
            .AsNoTracking()
            .Where(l => l.NotebookId == notebookId)
            .Select(l => l.HostFolderMountId)
            .ToListAsync(cancellationToken);
        var existingMountIdSet = existingMountIds.ToHashSet();

        var addedLinks = false;
        foreach (var mount in activeProjectMounts)
        {
            if (existingMountIdSet.Contains(mount.Id))
            {
                continue;
            }

            db.HostFolderMountLinks.Add(new HostFolderMountLink
            {
                HostFolderMountId = mount.Id,
                NotebookId = notebookId,
                LinkRelativePath = mount.LeafName,
                LinkPhysicalPath = Path.Combine(notebookRoot, mount.LeafName),
                Status = HostFolderMountLinkStatus.PendingRestart,
                LastCheckedUtc = now
            });
            addedLinks = true;
        }

        if (addedLinks)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await ReconcileNotebookMountsAsync(projectId, notebookId, cancellationToken);
    }

    public Task RepairStaleSymlinksAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        ReconcileProjectMountsAsync(projectId, cancellationToken);

    private async Task ReconcileMountsInternalAsync(
        ApplicationDbContext db,
        Guid? projectId,
        Guid? notebookId,
        Guid? mountId,
        CancellationToken cancellationToken)
    {
        var query = db.HostFolderMounts
            .Include(m => m.Links)
            .AsQueryable();

        if (projectId is Guid scopedProjectId)
        {
            query = query.Where(m => m.ProjectId == scopedProjectId);
        }

        if (mountId is Guid scopedMountId)
        {
            query = query.Where(m => m.Id == scopedMountId);
        }

        if (notebookId is Guid scopedNotebookId)
        {
            query = query.Where(m => m.Links.Any(l => l.NotebookId == scopedNotebookId));
        }

        query = query.Where(m => m.Status != HostFolderMountStatus.Removed);

        var mounts = await query.ToListAsync(cancellationToken);
        var affectedNotebookIds = new HashSet<Guid>();

        foreach (var mount in mounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var linksToReconcile = notebookId is Guid filterNotebookId
                ? mount.Links.Where(l => l.NotebookId == filterNotebookId).ToList()
                : mount.Links.ToList();

            if (linksToReconcile.Count == 0)
            {
                continue;
            }

            var sourcePresent = IsMountSourcePresent(mount);

            foreach (var link in linksToReconcile)
            {
                ReconcileLink(mount, link, sourcePresent);
                affectedNotebookIds.Add(link.NotebookId);
            }

            UpdateMountStatusFromLinks(mount, sourcePresent);
            mount.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (affectedNotebookIds.Count > 0)
        {
            await WriteMountsRegistriesForNotebooksAsync(db, affectedNotebookIds, cancellationToken);
        }
    }

    private void ReconcileLink(HostFolderMount mount, HostFolderMountLink link, bool sourcePresent)
    {
        link.LastCheckedUtc = DateTime.UtcNow;

        if (mount.Status is HostFolderMountStatus.PendingRemoval or HostFolderMountStatus.Removed)
        {
            ReconcileStaleLinkRemoval(link);
            return;
        }

        if (!sourcePresent)
        {
            ReconcileLinkWithMissingSource(mount, link);
            return;
        }

        ReconcileLinkWithSourcePresent(mount, link);
    }

    private void ReconcileLinkWithMissingSource(HostFolderMount mount, HostFolderMountLink link)
    {
        var notebookRoot = _pathResolver.GetNotebookRootPath(mount.ProjectId, link.NotebookId);
        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, mount.LeafName, out var linkPath))
        {
            link.Status = HostFolderMountLinkStatus.LinkError;
            link.ErrorMessage = "Mount link path is not under notebook root.";
            return;
        }

        link.LinkPhysicalPath = linkPath;
        link.LinkRelativePath = mount.LeafName;

        if (!TryResolvePhysicalSourcePath(mount, out var physicalSourcePath))
        {
            link.Status = HostFolderMountLinkStatus.LinkError;
            link.ErrorMessage =
                $"Mount source path '{mount.ContainerSourcePath}' is not under the configured host-mount root.";
            return;
        }

        if (PathExists(linkPath))
        {
            link.Status = HostFolderMountLinkStatus.LinkError;
            link.ErrorMessage = IsDirectorySymlinkPointingAtAny(
                linkPath,
                physicalSourcePath,
                mount.ContainerSourcePath)
                ? "Missing source"
                : $"Cannot link mount because '{mount.LeafName}' already exists in the notebook root.";
            return;
        }

        link.Status = HostFolderMountLinkStatus.PendingRestart;
        link.ErrorMessage = "Missing source";
    }

    private void ReconcileLinkWithSourcePresent(HostFolderMount mount, HostFolderMountLink link)
    {
        try
        {
            CreateDirectorySymlinkForLink(mount, link);
            link.Status = HostFolderMountLinkStatus.Linked;
            link.LastLinkedUtc = DateTime.UtcNow;
            link.ErrorMessage = null;
        }
        catch (DirectoryNotFoundException)
        {
            link.Status = HostFolderMountLinkStatus.PendingRestart;
            link.ErrorMessage = "Missing source";
        }
        catch (Exception ex)
        {
            link.Status = HostFolderMountLinkStatus.LinkError;
            link.ErrorMessage = ex.Message;
        }
    }

    private void ReconcileStaleLinkRemoval(HostFolderMountLink link)
    {
        try
        {
            RemoveDirectorySymlinkAtLinkPath(link.LinkPhysicalPath);
            link.Status = HostFolderMountLinkStatus.Unlinked;
            link.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            link.Status = HostFolderMountLinkStatus.UnlinkError;
            link.ErrorMessage = ex.Message;
        }
    }

    private bool IsMountSourcePresent(HostFolderMount mount) =>
        TryResolvePhysicalSourcePath(mount, out var physicalSourcePath)
        && Directory.Exists(physicalSourcePath);

    private static void UpdateMountStatusFromLinks(HostFolderMount mount, bool sourcePresent)
    {
        if (mount.Status is HostFolderMountStatus.PendingRemoval or HostFolderMountStatus.Removed)
        {
            if (mount.Links.Any(l => l.Status == HostFolderMountLinkStatus.UnlinkError))
            {
                mount.Status = HostFolderMountStatus.Error;
                mount.ErrorMessage ??= UnlinkFailureRemediation;
                return;
            }

            if (mount.Links.Count > 0 && mount.Links.All(l => l.Status == HostFolderMountLinkStatus.Unlinked))
            {
                mount.ErrorMessage = null;
                if (!sourcePresent)
                {
                    mount.Status = HostFolderMountStatus.Removed;
                    mount.RemovedUtc ??= DateTime.UtcNow;
                }
            }

            return;
        }

        if (!sourcePresent)
        {
            mount.Status = HostFolderMountStatus.PendingRestart;
            mount.ErrorMessage = "Missing source";
            return;
        }

        if (mount.Links.Count == 0)
        {
            mount.Status = HostFolderMountStatus.PendingRestart;
            mount.ErrorMessage = null;
            return;
        }

        if (mount.Links.All(l => l.Status == HostFolderMountLinkStatus.Linked))
        {
            mount.Status = HostFolderMountStatus.Active;
            mount.ErrorMessage = null;
            return;
        }

        if (mount.Links.Any(l => l.Status == HostFolderMountLinkStatus.LinkError))
        {
            mount.Status = HostFolderMountStatus.Error;
            return;
        }

        mount.Status = HostFolderMountStatus.PendingRestart;
        mount.ErrorMessage = null;
    }

    private static string BuildReconcileMessage(HostFolderMount mount)
    {
        if (!string.IsNullOrWhiteSpace(mount.ErrorMessage))
        {
            return mount.ErrorMessage;
        }

        var linkedCount = mount.Links.Count(l => l.Status == HostFolderMountLinkStatus.Linked);
        return $"Reconciled mount with {linkedCount}/{mount.Links.Count} links linked.";
    }

    private static string SanitizeComposeVolumeName(string mountKey) =>
        mountKey.Replace('-', '_');

    private static string BuildSmbDriverOptions(HostFolderMount mount)
    {
        var options = mount.NetworkOptions ?? "uid=1000,gid=1000,file_mode=0664,dir_mode=0775,vers=3.0";
        if (!string.IsNullOrWhiteSpace(mount.CredentialRef))
        {
            return $"{options},${{{mount.CredentialRef}}}";
        }

        return options;
    }

    private void CreateDirectorySymlinkForLink(HostFolderMount mount, HostFolderMountLink link)
    {
        var notebookRoot = _pathResolver.GetNotebookRootPath(mount.ProjectId, link.NotebookId);

        if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, mount.LeafName, out var linkPath))
        {
            throw new InvalidOperationException(
                $"Mount link path for leaf '{mount.LeafName}' is not under notebook root.");
        }

        if (!TryResolvePhysicalSourcePath(mount, out var physicalSourcePath))
        {
            throw new InvalidOperationException(
                $"Mount source path '{mount.ContainerSourcePath}' is not under the configured host-mount root.");
        }

        if (!Directory.Exists(physicalSourcePath))
        {
            throw new DirectoryNotFoundException(
                $"Mount source is not present at '{mount.ContainerSourcePath}'.");
        }

        if (PathExists(linkPath))
        {
            if (!IsDirectorySymlinkPointingAtAny(
                    linkPath,
                    physicalSourcePath,
                    mount.ContainerSourcePath))
            {
                throw new InvalidOperationException(
                    $"Cannot create mount link because '{mount.LeafName}' already exists in the notebook root.");
            }

            link.LinkPhysicalPath = linkPath;
            link.LinkRelativePath = mount.LeafName;
            return;
        }

        Directory.CreateSymbolicLink(linkPath, physicalSourcePath);
        link.LinkPhysicalPath = linkPath;
        link.LinkRelativePath = mount.LeafName;
    }

    private bool TryResolvePhysicalSourcePath(HostFolderMount mount, out string physicalSourcePath) =>
        HostMountSourcePath.TryResolvePhysicalSourcePath(
            _hostMountsRoot,
            mount.ContainerSourcePath,
            out physicalSourcePath);

    private async Task<bool> TryUnlinkAllMountLinksAsync(
        ApplicationDbContext db,
        HostFolderMount mount,
        CancellationToken cancellationToken)
    {
        var unlinkFailed = false;
        var affectedNotebookIds = new HashSet<Guid>();

        foreach (var link in mount.Links)
        {
            affectedNotebookIds.Add(link.NotebookId);

            try
            {
                RemoveDirectorySymlinkAtLinkPath(link.LinkPhysicalPath);
                link.Status = HostFolderMountLinkStatus.Unlinked;
                link.ErrorMessage = null;
            }
            catch (Exception ex)
            {
                link.Status = HostFolderMountLinkStatus.UnlinkError;
                link.ErrorMessage = ex.Message;
                link.LastCheckedUtc = DateTime.UtcNow;
                unlinkFailed = true;
            }
        }

        mount.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (affectedNotebookIds.Count > 0)
        {
            await WriteMountsRegistriesForNotebooksAsync(db, affectedNotebookIds, cancellationToken);
        }

        return unlinkFailed;
    }

    private static void RemoveDirectorySymlinkAtLinkPath(string linkPhysicalPath)
    {
        if (string.IsNullOrWhiteSpace(linkPhysicalPath))
        {
            return;
        }

        if (!Directory.Exists(linkPhysicalPath) && !File.Exists(linkPhysicalPath))
        {
            return;
        }

        var attributes = File.GetAttributes(linkPhysicalPath);
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"Refusing to remove '{linkPhysicalPath}' because it is not a symlink.");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(linkPhysicalPath);
        }
        else
        {
            File.Delete(linkPhysicalPath);
        }
    }

    private static bool PathExists(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            return true;
        }

        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsDirectorySymlinkPointingAtAny(string linkPath, params string[] expectedTargets) =>
        expectedTargets.Any(expectedTarget => IsDirectorySymlinkPointingAt(linkPath, expectedTarget));

    private static bool IsDirectorySymlinkPointingAt(string linkPath, string expectedTarget)
    {
        if (!PathExists(linkPath))
        {
            return false;
        }

        var attributes = File.GetAttributes(linkPath);
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        var linkTarget = Directory.ResolveLinkTarget(linkPath, returnFinalTarget: false)
            ?? File.ResolveLinkTarget(linkPath, returnFinalTarget: false);
        if (linkTarget == null)
        {
            return false;
        }

        var actualTarget = linkTarget.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedExpected = expectedTarget.TrimEnd('/', '\\');
        return string.Equals(actualTarget, normalizedExpected, StringComparison.Ordinal);
    }

    private async Task WriteMountsRegistriesForNotebooksAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<Guid> notebookIds,
        CancellationToken cancellationToken)
    {
        foreach (var notebookId in notebookIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteMountsRegistryForNotebookAsync(db, notebookId, cancellationToken);
        }
    }

    private async Task WriteMountsRegistryForNotebookAsync(
        ApplicationDbContext db,
        Guid notebookId,
        CancellationToken cancellationToken)
    {
        var notebook = await db.Notebooks.AsNoTracking()
            .Where(n => n.Id == notebookId)
            .Select(n => new { n.ProjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (notebook == null)
        {
            throw new KeyNotFoundException($"Notebook {notebookId} was not found.");
        }

        var linkedMounts = (await db.HostFolderMountLinks
            .AsNoTracking()
            .Where(l => l.NotebookId == notebookId && l.Status == HostFolderMountLinkStatus.Linked)
            .Join(
                db.HostFolderMounts.AsNoTracking(),
                link => link.HostFolderMountId,
                mount => mount.Id,
                (link, mount) => new
                {
                    mount.Id,
                    mount.LeafName,
                    link.LinkRelativePath,
                    mount.ContainerSourcePath
                })
            .ToListAsync(cancellationToken))
            .OrderBy(entry => entry.LeafName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new NotebookMountsRegistryEntry
            {
                MountId = entry.Id,
                LeafName = entry.LeafName,
                LinkRelativePath = entry.LinkRelativePath,
                ContainerSourcePath = entry.ContainerSourcePath,
                Writable = true
            })
            .ToList();

        var notebookRoot = _pathResolver.GetNotebookRootPath(notebook.ProjectId, notebookId);
        _registryWriter.WriteRegistry(
            notebookRoot,
            new NotebookMountsRegistry
            {
                SchemaVersion = NotebookMountsRegistry.CurrentSchemaVersion,
                Mounts = linkedMounts
            });
    }
}
