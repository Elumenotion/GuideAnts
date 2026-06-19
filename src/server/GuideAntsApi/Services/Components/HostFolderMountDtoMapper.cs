using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Components;

public static class HostFolderMountDtoMapper
{
    public static HostFolderMountSummaryDto ToSummaryDto(HostFolderMount mount) =>
        new(
            mount.Id,
            mount.ProjectId,
            mount.NotebookId,
            mount.Scope,
            mount.SourceKind,
            mount.DisplayName,
            mount.LeafName,
            mount.MountKey,
            mount.ContainerSourcePath,
            mount.Status,
            mount.CreatedUtc,
            mount.UpdatedUtc,
            mount.ErrorMessage);

    public static HostFolderMountDetailDto ToDetailDto(HostFolderMount mount, bool includeAdminSensitiveFields)
    {
        string? hostPath = null;
        string? credentialRef = null;
        if (includeAdminSensitiveFields)
        {
            hostPath = mount.SourceSpec;
            credentialRef = mount.CredentialRef;
        }

        var links = mount.Links
            .Select(link => new HostFolderMountLinkSummaryDto(
                link.Id,
                link.NotebookId,
                link.LinkRelativePath,
                link.Status,
                link.ErrorMessage))
            .ToList();

        return new HostFolderMountDetailDto(
            mount.Id,
            mount.ProjectId,
            mount.NotebookId,
            mount.Scope,
            mount.SourceKind,
            mount.DisplayName,
            mount.LeafName,
            mount.MountKey,
            mount.ContainerSourcePath,
            mount.Status,
            mount.CreatedUtc,
            mount.UpdatedUtc,
            mount.RemovedUtc,
            mount.ErrorMessage,
            hostPath,
            mount.NetworkDevice,
            mount.NetworkOptions,
            credentialRef,
            links);
    }
}
