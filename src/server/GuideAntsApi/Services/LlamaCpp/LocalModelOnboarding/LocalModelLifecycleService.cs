using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ILocalModelLifecycleService
{
    Task<LlamaInstallationDetailDto> GetInstallationDetailAsync(string modelId, CancellationToken cancellationToken = default);

    Task<LifecycleOperationResponseDto> StartChangeQuantAsync(
        string modelId,
        ChangeQuantRequestDto request,
        CancellationToken cancellationToken = default);

    Task<LifecycleOperationResponseDto> StartRepairAsync(
        string modelId,
        RepairInstallationRequestDto request,
        CancellationToken cancellationToken = default);

    Task<AdoptPreviewResponseDto> PreviewAdoptAsync(
        string modelId,
        string catalogId,
        string catalogVersion,
        CancellationToken cancellationToken = default);

    Task<LlamaInstallationDetailDto> AdoptAsync(
        string modelId,
        AdoptInstallationRequestDto request,
        CancellationToken cancellationToken = default);
}

public sealed class LocalModelLifecycleService : ILocalModelLifecycleService
{
    private readonly ApplicationDbContext _db;
    private readonly ILocalModelInstallationService _installationService;
    private readonly ILocalModelLifecycleOperationService _operationService;
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly ILlamaRuntimeInventoryService _inventoryService;
    private readonly ILlamaRuntimeCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocalModelLifecycleService> _logger;

    public LocalModelLifecycleService(
        ApplicationDbContext db,
        ILocalModelInstallationService installationService,
        ILocalModelLifecycleOperationService operationService,
        ILlamaRuntimeAdminClient adminClient,
        IHuggingFaceTokenResolver tokenResolver,
        ILlamaRuntimeInventoryService inventoryService,
        ILlamaRuntimeCoordinator coordinator,
        IServiceScopeFactory scopeFactory,
        ILogger<LocalModelLifecycleService> logger)
    {
        _db = db;
        _installationService = installationService;
        _operationService = operationService;
        _adminClient = adminClient;
        _tokenResolver = tokenResolver;
        _inventoryService = inventoryService;
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<LlamaInstallationDetailDto> GetInstallationDetailAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        _installationService.GetDetailAsync(modelId, cancellationToken);

    public async Task<LifecycleOperationResponseDto> StartChangeQuantAsync(
        string modelId,
        ChangeQuantRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var installation = await LoadInstallationAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(installation.CatalogId) || string.IsNullOrWhiteSpace(installation.CatalogVersion))
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.ProvenanceUnknown,
                "Curated catalog identity is missing from installation provenance.",
                "Repair provenance or remain operator-managed.",
                statusCode: 422);
        }

        await EnsureNoInFlightOperationAsync(installation.RouterModelId!, cancellationToken).ConfigureAwait(false);

        var quantId = request.QuantId.Trim();
        var resolvedRevision = request.ResolvedRevision.Trim();
        if (string.Equals(installation.QuantId, quantId, StringComparison.Ordinal)
            && string.Equals(installation.ResolvedRevision, resolvedRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalModelLifecycleException(
                "QUANT_UNCHANGED",
                "Selected quant and commit match the active installation.",
                "Choose a different quant group or revision.",
                statusCode: 409);
        }

        var hfToken = _tokenResolver.Resolve();
        if (string.IsNullOrWhiteSpace(hfToken))
        {
            throw new LocalModelLifecycleException(
                CuratedInstallErrorCodes.HuggingFaceTokenMissing,
                "No Hugging Face token is configured.",
                "Open Connections → Hugging Face and save a token before retrying.",
                statusCode: 403);
        }

        var quants = await _adminClient
            .GetCatalogQuantsAsync(
                installation.CatalogId,
                installation.CatalogVersion,
                hfToken,
                cancellationToken,
                resolvedRevision: resolvedRevision)
            .ConfigureAwait(false);

        if (!string.Equals(quants.ResolvedRevision, resolvedRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalModelLifecycleException(
                CuratedInstallErrorCodes.CommitUnavailable,
                $"Resolved revision '{resolvedRevision}' is not available.",
                "Refresh quant selection and retry.",
                statusCode: 422);
        }

        var quant = quants.Quants.FirstOrDefault(q => string.Equals(q.Id, quantId, StringComparison.Ordinal));
        if (quant is null || quant.Files.Count == 0)
        {
            throw new LocalModelLifecycleException(
                CuratedInstallErrorCodes.QuantMissing,
                $"Quant '{quantId}' is not available at commit '{resolvedRevision}'.",
                "Refresh quant selection and choose an available group.",
                statusCode: 422);
        }

        var catalog = await _adminClient.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var definition = catalog.Models.FirstOrDefault(m => string.Equals(m.Id, installation.CatalogId, StringComparison.Ordinal));
        if (definition is null)
        {
            throw new LocalModelLifecycleException(
                CuratedInstallErrorCodes.CatalogDefinitionNotFound,
                $"Catalog definition '{installation.CatalogId}' was not found.",
                "Refresh the catalog and retry.",
                statusCode: 404);
        }

        var routerPreset = RouterPresetValidator.ValidateAndNormalize(definition.Defaults.RouterPreset);
        var modelFiles = quant.Files.Select(f => f.Path).ToList();
        var mmprojFiles = quants.Projector is null ? Array.Empty<string>() : new[] { quants.Projector.Path };

        var oldPaths = InstallationArtifactRecords.Parse(installation.ModelArtifactsJson)
            .Concat(InstallationArtifactRecords.Parse(installation.ProjectorArtifactsJson))
            .Select(a => a.RepositoryPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var newPaths = modelFiles.Concat(mmprojFiles).ToHashSet(StringComparer.Ordinal);
        var obsoletePaths = oldPaths.Where(p => !newPaths.Contains(p)).ToList();

        var immutableInput = new ChangeQuantImmutableInput(
            ModelId: installation.ModelId,
            CatalogId: installation.CatalogId,
            CatalogVersion: installation.CatalogVersion,
            OldQuantId: installation.QuantId ?? string.Empty,
            NewQuantId: quantId,
            NewQuantLabel: quant.Label,
            Repository: quants.Repository,
            ResolvedRevision: resolvedRevision,
            ModelFiles: modelFiles,
            MmprojFiles: mmprojFiles,
            RouterModelId: installation.RouterModelId!,
            RuntimeProfileId: definition.Defaults.RuntimeProfileId,
            TargetDirectory: installation.TargetDirectory!,
            RouterPreset: routerPreset,
            ObsoleteRepositoryPaths: obsoletePaths,
            ArtifactMetadata: BuildArtifactMetadata(quant, quants.Projector));

        var wasLoaded = await CaptureLoadedStateAsync(installation.RouterModelId!, cancellationToken).ConfigureAwait(false);
        var operation = await _operationService
            .CreateChangeQuantOperationAsync(immutableInput, wasLoaded, cancellationToken)
            .ConfigureAwait(false);

        var operationId = operation.OperationId;
        QueueBackgroundLifecycleReconciliation(operationId, "Background change-quant reconciliation failed for {OperationId}.");

        return new LifecycleOperationResponseDto(operation.OperationId.ToString("D"), operation.Status);
    }

    public async Task<LifecycleOperationResponseDto> StartRepairAsync(
        string modelId,
        RepairInstallationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirm)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.ConfirmationRequired,
                "Repair requires explicit confirmation.",
                "Resubmit with confirm=true.",
                statusCode: 409);
        }

        var installation = await LoadInstallationAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(installation.Repository)
            || string.IsNullOrWhiteSpace(installation.ResolvedRevision)
            || string.IsNullOrWhiteSpace(installation.RouterModelId)
            || string.IsNullOrWhiteSpace(installation.TargetDirectory))
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.ProvenanceUnknown,
                "Repair requires recorded repository, commit, alias, and target directory.",
                "Remain operator-managed and reinstall explicitly if needed.",
                statusCode: 422);
        }

        await EnsureNoInFlightOperationAsync(installation.RouterModelId, cancellationToken).ConfigureAwait(false);

        var modelFiles = InstallationArtifactRecords.Parse(installation.ModelArtifactsJson)
            .Select(a => a.RepositoryPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var mmprojFiles = InstallationArtifactRecords.Parse(installation.ProjectorArtifactsJson)
            .Select(a => a.RepositoryPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (modelFiles.Count == 0)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.ProvenanceUnknown,
                "Repair requires recorded model artifact paths.",
                "Reinstall the model with explicit artifact provenance.",
                statusCode: 422);
        }

        var catalog = await _adminClient.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var definition = string.IsNullOrWhiteSpace(installation.CatalogId)
            ? null
            : catalog.Models.FirstOrDefault(m => string.Equals(m.Id, installation.CatalogId, StringComparison.Ordinal));

        var preset = InstallationArtifactRecords.ParsePresetSnapshot(installation.RouterPresetSnapshotJson);
        var immutableInput = new RepairImmutableInput(
            ModelId: installation.ModelId,
            Repository: installation.Repository,
            ResolvedRevision: installation.ResolvedRevision,
            ModelFiles: modelFiles,
            MmprojFiles: mmprojFiles,
            RouterModelId: installation.RouterModelId,
            RuntimeProfileId: definition?.Defaults.RuntimeProfileId ?? string.Empty,
            TargetDirectory: installation.TargetDirectory,
            RouterPreset: preset);

        var wasLoaded = await CaptureLoadedStateAsync(installation.RouterModelId, cancellationToken).ConfigureAwait(false);
        var operation = await _operationService
            .CreateRepairOperationAsync(immutableInput, wasLoaded, cancellationToken)
            .ConfigureAwait(false);

        var operationId = operation.OperationId;
        QueueBackgroundLifecycleReconciliation(operationId, "Background repair reconciliation failed for {OperationId}.");

        return new LifecycleOperationResponseDto(operation.OperationId.ToString("D"), operation.Status);
    }

    public async Task<AdoptPreviewResponseDto> PreviewAdoptAsync(
        string modelId,
        string catalogId,
        string catalogVersion,
        CancellationToken cancellationToken = default)
    {
        var installation = await LoadInstallationAsync(modelId, cancellationToken).ConfigureAwait(false);
        var catalog = await _adminClient.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var definition = catalog.Models.FirstOrDefault(m => string.Equals(m.Id, catalogId.Trim(), StringComparison.Ordinal));

        var differences = new List<AdoptDiffFieldDto>();
        var blockers = new List<string>();

        if (definition is null)
        {
            blockers.Add($"Catalog definition '{catalogId}' was not found.");
            return new AdoptPreviewResponseDto(modelId, catalogId, catalogVersion, differences, false, blockers);
        }

        if (!string.Equals(catalog.CatalogVersion, catalogVersion.Trim(), StringComparison.Ordinal))
        {
            blockers.Add($"Catalog version '{catalogVersion}' does not match shipped version '{catalog.CatalogVersion}'.");
        }

        CompareField(differences, blockers, "routerModelId", installation.RouterModelId, definition.Defaults.RouterModelId, verifiable: true);

        if (string.IsNullOrWhiteSpace(installation.Repository))
        {
            differences.Add(new AdoptDiffFieldDto("repository", null, definition.Source.Repository, false, "Repository provenance is unknown."));
            blockers.Add("Repository provenance is unknown.");
        }
        else
        {
            CompareField(differences, blockers, "repository", installation.Repository, definition.Source.Repository, verifiable: true);
        }

        if (string.IsNullOrWhiteSpace(installation.ResolvedRevision))
        {
            differences.Add(new AdoptDiffFieldDto("resolvedRevision", null, definition.Source.Revision, false, "Resolved revision is unknown."));
            blockers.Add("Resolved revision is unknown.");
        }
        else if (!string.IsNullOrWhiteSpace(definition.Source.Revision))
        {
            CompareField(differences, blockers, "requestedRevision", installation.RequestedRevision, definition.Source.Revision, verifiable: false);
        }

        if (string.IsNullOrWhiteSpace(installation.QuantId))
        {
            differences.Add(new AdoptDiffFieldDto("quantId", null, null, false, "Quant provenance is unknown."));
            blockers.Add("Quant provenance is unknown.");
        }

        var preset = InstallationArtifactRecords.ParsePresetSnapshot(installation.RouterPresetSnapshotJson);
        foreach (var (key, curatedValue) in definition.Defaults.RouterPreset)
        {
            preset.TryGetValue(key, out var currentValue);
            CompareField(differences, blockers, $"preset.{key}", currentValue, curatedValue, verifiable: currentValue is not null);
        }

        var canAdopt = blockers.Count == 0
            && differences.All(d => d.Verifiable || d.CurrentValue is null);

        return new AdoptPreviewResponseDto(modelId, catalogId, catalogVersion, differences, canAdopt, blockers);
    }

    public async Task<LlamaInstallationDetailDto> AdoptAsync(
        string modelId,
        AdoptInstallationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirm)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.ConfirmationRequired,
                "Adoption requires explicit confirmation.",
                "Review the adoption diff and resubmit with confirm=true.",
                statusCode: 409);
        }

        var preview = await PreviewAdoptAsync(modelId, request.CatalogId, request.CatalogVersion, cancellationToken)
            .ConfigureAwait(false);
        if (!preview.CanAdopt)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.AdoptionBlocked,
                "Adoption is blocked until required provenance can be verified.",
                string.Join(" ", preview.Blockers),
                statusCode: 409);
        }

        var installation = await _db.LocalModelInstallations
            .SingleAsync(i => i.ModelId == modelId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        installation.ManagementMode = "curated";
        installation.CatalogId = request.CatalogId.Trim();
        installation.CatalogVersion = request.CatalogVersion.Trim();
        installation.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var model = await _db.Models.AsNoTracking()
            .SingleAsync(m => m.ModelId == modelId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return await _installationService.GetDetailAsync(model.ModelId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalModelInstallation> LoadInstallationAsync(string modelId, CancellationToken cancellationToken)
    {
        var normalized = modelId.Trim();
        var installation = await _db.LocalModelInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.ModelId == normalized, cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.InstallationNotFound,
                $"Model '{normalized}' has no installation provenance record.",
                "Install the model before running lifecycle actions.",
                statusCode: 404);
        }

        return installation;
    }

    private async Task EnsureNoInFlightOperationAsync(string routerModelId, CancellationToken cancellationToken)
    {
        var inFlight = await _db.LocalModelOperations
            .AsNoTracking()
            .AnyAsync(
                o => o.RouterModelId == routerModelId
                     && o.Status != "completed"
                     && o.Status != "failed",
                cancellationToken)
            .ConfigureAwait(false);

        if (inFlight)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.OperationInFlight,
                $"An operation is already in progress for alias '{routerModelId}'.",
                "Wait for the in-flight operation to complete, then retry.",
                statusCode: 409);
        }

        if (_coordinator.IsAliasLocked(routerModelId))
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.AliasLockConflict,
                $"Alias '{routerModelId}' is locked by a runtime operation.",
                "Wait for the runtime operation to complete, then retry.",
                statusCode: 409);
        }
    }

    private async Task<bool> CaptureLoadedStateAsync(string routerModelId, CancellationToken cancellationToken)
    {
        var inventory = await _inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var row = inventory.FirstOrDefault(i => string.Equals(i.RouterModelId, routerModelId, StringComparison.Ordinal));
        return row is not null && string.Equals(row.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);
    }

    private static void CompareField(
        ICollection<AdoptDiffFieldDto> differences,
        ICollection<string> blockers,
        string field,
        string? current,
        string? curated,
        bool verifiable)
    {
        if (string.Equals(current?.Trim(), curated?.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        differences.Add(new AdoptDiffFieldDto(
            field,
            current,
            curated,
            verifiable,
            verifiable ? null : "Value cannot be verified from installation provenance."));

        if (!verifiable)
        {
            blockers.Add($"{field} cannot be verified.");
        }
    }

    private static IReadOnlyList<CuratedArtifactMetadataInput>? BuildArtifactMetadata(
        LlamaQuantGroupDto quant,
        LlamaProjectorArtifactDto? projector)
    {
        var metadata = quant.Files
            .Select(f => new CuratedArtifactMetadataInput(f.Path, f.Size))
            .ToList();
        if (projector is not null)
        {
            metadata.Add(new CuratedArtifactMetadataInput(projector.Path, projector.Size));
        }

        return metadata.Count == 0 ? null : metadata;
    }

    private void QueueBackgroundLifecycleReconciliation(Guid operationId, string failureMessageTemplate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var operationService = scope.ServiceProvider.GetRequiredService<ILocalModelLifecycleOperationService>();
                await operationService
                    .ReconcileLifecycleOperationAsync(operationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, failureMessageTemplate, operationId);
            }
        }, CancellationToken.None);
    }
}
