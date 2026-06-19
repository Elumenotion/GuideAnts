using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Models;

public sealed class HostFolderMountCreateEndpointRequest
{
    public Guid? NotebookId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string HostPath { get; set; } = string.Empty;

    public string? LeafName { get; set; }
}

public sealed record HostFolderMountCreateEndpointResponse(
    Guid MountId,
    HostFolderMountStatus Status,
    string LeafName,
    string ContainerSourcePath,
    string Command);

public sealed record HostFolderMountRemoveEndpointResponse(
    Guid MountId,
    HostFolderMountStatus Status,
    string Command);

public sealed record HostFolderMountApplyCommandEndpointResponse(
    Guid MountId,
    HostFolderMountStatus Status,
    string LeafName,
    string ContainerSourcePath,
    string Command);

public sealed record HostFolderMountReconcileEndpointResponse(
    Guid MountId,
    HostFolderMountStatus Status,
    string Message);

public sealed record HostFolderMountSummaryDto(
    Guid MountId,
    Guid ProjectId,
    Guid? NotebookId,
    HostFolderMountScope Scope,
    SourceKind SourceKind,
    string DisplayName,
    string LeafName,
    string MountKey,
    string ContainerSourcePath,
    HostFolderMountStatus Status,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? ErrorMessage);

public sealed record HostFolderMountLinkSummaryDto(
    Guid LinkId,
    Guid NotebookId,
    string LinkRelativePath,
    HostFolderMountLinkStatus Status,
    string? ErrorMessage);

public sealed record HostFolderMountDetailDto(
    Guid MountId,
    Guid ProjectId,
    Guid? NotebookId,
    HostFolderMountScope Scope,
    SourceKind SourceKind,
    string DisplayName,
    string LeafName,
    string MountKey,
    string ContainerSourcePath,
    HostFolderMountStatus Status,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? RemovedUtc,
    string? ErrorMessage,
    string? HostPath,
    string? NetworkDevice,
    string? NetworkOptions,
    string? CredentialRef,
    IReadOnlyList<HostFolderMountLinkSummaryDto> Links);

public sealed record HostFolderMountComposeOverridePlanEndpointResponse(
    Guid MountId,
    Guid ProjectId,
    string MountKey,
    string SourceKind,
    string HostPath);
