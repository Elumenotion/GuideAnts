using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Components;

public interface IHostFolderMountService
{
    Task<HostFolderMountCreateResult> CreateMountAsync(
        Guid projectId,
        HostFolderMountCreateRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostFolderMount>> ListMountsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<HostFolderMount?> GetMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default);

    Task<HostFolderMountApplyCommandResult> GetApplyCommandAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default);

    Task<HostFolderMountRemoveCommandResult> BeginRemoveMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default);

    Task<HostFolderMountReconcileResult> ReconcileMountAsync(
        Guid projectId,
        Guid mountId,
        CancellationToken cancellationToken = default);

    Task<HostFolderMountComposeOverridePlanScriptResponse?> GetComposeOverridePlanForScriptAsync(
        Guid mountId,
        CancellationToken cancellationToken = default);

    Task<HostFolderMountValidationResult> ValidateLeafNameAsync(
        Guid projectId,
        HostFolderMountScope scope,
        Guid? notebookId,
        string leafName,
        Guid? excludeMountId = null,
        CancellationToken cancellationToken = default);

    HostFolderMountComposeOverridePlan BuildComposeOverridePlan(HostFolderMount mount);

    string BuildApplyCommandText(Guid mountId, Guid projectId, string hostPath);

    string BuildRemoveCommandText(Guid mountId);

    Task CreateSymlinksForMountAsync(Guid mountId, CancellationToken cancellationToken = default);

    Task RemoveSymlinksForMountAsync(Guid mountId, CancellationToken cancellationToken = default);

    Task UpdateMountsRegistryAsync(Guid mountId, CancellationToken cancellationToken = default);

    Task ReconcileProjectMountsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task ReconcileNotebookMountsAsync(
        Guid projectId,
        Guid notebookId,
        CancellationToken cancellationToken = default);

    Task ReconcileAllMountsAsync(CancellationToken cancellationToken = default);

    Task ApplyProjectScopedMappingsToNewNotebookAsync(
        Guid projectId,
        Guid notebookId,
        CancellationToken cancellationToken = default);

    Task RepairStaleSymlinksAsync(Guid projectId, CancellationToken cancellationToken = default);
}
