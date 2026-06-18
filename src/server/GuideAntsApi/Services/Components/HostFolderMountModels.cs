using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Components;

public sealed record HostFolderMountCreateRequest(
    HostFolderMountScope Scope,
    Guid? NotebookId,
    string HostPath,
    string? LeafName,
    SourceKind SourceKind = SourceKind.LocalPath);

public sealed record HostFolderMountCreateResult(
    Guid MountId,
    HostFolderMountStatus Status,
    string LeafName,
    string MountKey,
    string ContainerSourcePath,
    string ApplyCommand);

public sealed record HostFolderMountRemoveCommandResult(
    Guid MountId,
    HostFolderMountStatus Status,
    string RemoveCommand);

public sealed record HostFolderMountApplyCommandResult(
    Guid MountId,
    HostFolderMountStatus Status,
    string LeafName,
    string ContainerSourcePath,
    string ApplyCommand);

public sealed record HostFolderMountReconcileResult(
    Guid MountId,
    HostFolderMountStatus Status,
    string Message);

public sealed record HostFolderMountComposeOverridePlanScriptResponse(
    Guid MountId,
    Guid ProjectId,
    string MountKey,
    SourceKind SourceKind,
    string HostPath);

public sealed record HostFolderMountValidationResult(bool IsValid, string? ErrorCode, string? ErrorMessage)
{
    public static HostFolderMountValidationResult Ok() => new(true, null, null);

    public static HostFolderMountValidationResult Fail(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

public sealed record HostFolderMountComposeBindVolume(
    string ServiceName,
    string HostSourcePath,
    string ContainerTargetPath,
    bool ReadOnly);

public sealed record HostFolderMountComposeSmbVolume(
    string VolumeName,
    string Device,
    string DriverOptions,
    string? CredentialRef);

public sealed record HostFolderMountComposeOverridePlan(
    Guid MountId,
    Guid ProjectId,
    string MountKey,
    SourceKind SourceKind,
    string ContainerTargetPath,
    IReadOnlyList<string> AffectedServices,
    IReadOnlyList<HostFolderMountComposeBindVolume>? BindVolumes,
    HostFolderMountComposeSmbVolume? SmbVolume);
