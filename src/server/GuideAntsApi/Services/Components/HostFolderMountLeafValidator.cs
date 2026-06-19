using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Components;

public sealed class HostFolderMountLeafValidator
{
    private static readonly HashSet<string> ReservedLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".guideants",
        "Output",
        "Runs",
        "Resources",
        "files"
    };

    private static readonly HashSet<HostFolderMountLinkStatus> ActiveLinkStatuses =
    [
        HostFolderMountLinkStatus.PendingRestart,
        HostFolderMountLinkStatus.PendingLink,
        HostFolderMountLinkStatus.Linked
    ];

    private static readonly HostFolderMountStatus[] InactiveMountStatuses =
    [
        HostFolderMountStatus.Removed
    ];

    public HostFolderMountValidationResult ValidateFormat(string? leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName))
        {
            return HostFolderMountValidationResult.Fail(
                "leaf_empty",
                "Leaf name must not be empty.");
        }

        if (leafName.Contains('\0', StringComparison.Ordinal))
        {
            return HostFolderMountValidationResult.Fail(
                "leaf_null_char",
                "Leaf name must not contain null characters.");
        }

        if (leafName is "." or "..")
        {
            return HostFolderMountValidationResult.Fail(
                "leaf_reserved_dot",
                "Leaf name must not be '.' or '..'.");
        }

        if (leafName.Contains('/', StringComparison.Ordinal)
            || leafName.Contains('\\', StringComparison.Ordinal))
        {
            return HostFolderMountValidationResult.Fail(
                "leaf_path_separator",
                "Leaf name must not contain path separators.");
        }

        if (ReservedLeafNames.Contains(leafName))
        {
            return HostFolderMountValidationResult.Fail(
                "leaf_reserved_name",
                $"Leaf name '{leafName}' is reserved.");
        }

        return HostFolderMountValidationResult.Ok();
    }

    public async Task<HostFolderMountValidationResult> ValidateCollisionsAsync(
        ApplicationDbContext db,
        IStoragePathResolver pathResolver,
        Guid projectId,
        IReadOnlyList<Guid> targetNotebookIds,
        string leafName,
        Guid? excludeMountId = null,
        CancellationToken cancellationToken = default)
    {
        var formatResult = ValidateFormat(leafName);
        if (!formatResult.IsValid)
        {
            return formatResult;
        }

        foreach (var notebookId in targetNotebookIds)
        {
            var notebookRoot = pathResolver.GetNotebookRootPath(projectId, notebookId);
            var candidatePath = Path.Combine(notebookRoot, leafName);

            if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
            {
                return HostFolderMountValidationResult.Fail(
                    "leaf_filesystem_collision",
                    $"Leaf name '{leafName}' collides with an existing file or folder in notebook {notebookId}.");
            }

            var leafLower = leafName.ToLowerInvariant();

            var hasNotebookFileCollision = await db.NotebookFiles
                .AsNoTracking()
                .Where(f => f.NotebookId == notebookId)
                .AnyAsync(
                    f => f.RelativePath.ToLower() == leafLower
                         || f.RelativePath.ToLower().StartsWith(leafLower + "/")
                         || f.RelativePath.ToLower().StartsWith(leafLower + "\\"),
                    cancellationToken);

            if (hasNotebookFileCollision)
            {
                return HostFolderMountValidationResult.Fail(
                    "leaf_notebook_file_collision",
                    $"Leaf name '{leafName}' collides with an existing notebook file in notebook {notebookId}.");
            }

            var activeLinkQuery = db.HostFolderMountLinks
                .AsNoTracking()
                .Where(l => l.NotebookId == notebookId
                            && (l.Status == HostFolderMountLinkStatus.PendingRestart
                                || l.Status == HostFolderMountLinkStatus.PendingLink
                                || l.Status == HostFolderMountLinkStatus.Linked)
                            && l.LinkRelativePath.ToLower() == leafLower);

            if (excludeMountId is Guid excludedMountId)
            {
                activeLinkQuery = activeLinkQuery.Where(l => l.HostFolderMountId != excludedMountId);
            }

            var hasActiveLinkCollision = await activeLinkQuery.AnyAsync(cancellationToken);

            if (hasActiveLinkCollision)
            {
                return HostFolderMountValidationResult.Fail(
                    "leaf_active_mapping_collision",
                    $"Leaf name '{leafName}' collides with another active mapping in notebook {notebookId}.");
            }
        }

        return HostFolderMountValidationResult.Ok();
    }

    public static bool IsActiveMountStatus(HostFolderMountStatus status) =>
        !InactiveMountStatuses.Contains(status);

    public static IReadOnlyList<Guid> ResolveTargetNotebookIds(
        HostFolderMountScope scope,
        Guid? notebookId,
        IReadOnlyList<Guid> projectNotebookIds)
    {
        if (scope == HostFolderMountScope.Notebook)
        {
            if (notebookId is not Guid singleNotebookId)
            {
                throw new ArgumentException("Notebook scope requires a notebook id.", nameof(notebookId));
            }

            return [singleNotebookId];
        }

        return projectNotebookIds;
    }
}
