using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ILocalModelLifecycleOperationService
{
    Task<LocalModelOperation> CreateChangeQuantOperationAsync(
        ChangeQuantImmutableInput immutableInput,
        bool wasLoadedAtStart,
        CancellationToken cancellationToken = default);

    Task<LocalModelOperation> CreateRepairOperationAsync(
        RepairImmutableInput immutableInput,
        bool wasLoadedAtStart,
        CancellationToken cancellationToken = default);

    Task<LocalModelOperation> CreateCustomInstallOperationAsync(
        CustomInstallImmutableInput immutableInput,
        CancellationToken cancellationToken = default);

    Task<LlamaOperationStatusDto> ReconcileLifecycleOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task ReconcileInFlightLifecycleOperationsAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalModelLifecycleOperationService : ILocalModelLifecycleOperationService
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
    };

    private static readonly HashSet<string> InFlightStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued",
        "resolvingFiles",
        "downloading",
        "validating",
        "registeringAlias",
        "provenanceFinalization",
        "obsoleteCleanup",
        "loadRestore",
    };

    private static readonly HashSet<string> ActiveDownloadStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued",
        "resolvingFiles",
        "downloading",
        "validating",
        "registeringAlias",
    };

    private readonly ApplicationDbContext _db;
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly ILlamaServerRuntimeClient _llamaClient;
    private readonly ILlamaRuntimeCoordinator _coordinator;
    private readonly ILogger<LocalModelLifecycleOperationService> _logger;

    public LocalModelLifecycleOperationService(
        ApplicationDbContext db,
        ILlamaRuntimeAdminClient adminClient,
        IHuggingFaceTokenResolver tokenResolver,
        ILlamaServerRuntimeClient llamaClient,
        ILlamaRuntimeCoordinator coordinator,
        IRuntimeProfileResolver runtimeProfileResolver,
        ILogger<LocalModelLifecycleOperationService> logger)
    {
        _db = db;
        _adminClient = adminClient;
        _tokenResolver = tokenResolver;
        _llamaClient = llamaClient;
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task<LocalModelOperation> CreateChangeQuantOperationAsync(
        ChangeQuantImmutableInput immutableInput,
        bool wasLoadedAtStart,
        CancellationToken cancellationToken = default) =>
        await CreateLifecycleOperationAsync(
            LocalModelOperationKinds.ChangeQuant,
            immutableInput.ModelId,
            immutableInput.RouterModelId,
            immutableInput.ToJson(),
            wasLoadedAtStart,
            cancellationToken).ConfigureAwait(false);

    public async Task<LocalModelOperation> CreateRepairOperationAsync(
        RepairImmutableInput immutableInput,
        bool wasLoadedAtStart,
        CancellationToken cancellationToken = default) =>
        await CreateLifecycleOperationAsync(
            LocalModelOperationKinds.Repair,
            immutableInput.ModelId,
            immutableInput.RouterModelId,
            immutableInput.ToJson(),
            wasLoadedAtStart,
            cancellationToken).ConfigureAwait(false);

    public async Task<LocalModelOperation> CreateCustomInstallOperationAsync(
        CustomInstallImmutableInput immutableInput,
        CancellationToken cancellationToken = default) =>
        await CreateLifecycleOperationAsync(
            LocalModelOperationKinds.CustomInstall,
            immutableInput.CatalogModelId,
            immutableInput.RouterModelId,
            immutableInput.ToJson(),
            wasLoadedAtStart: false,
            cancellationToken).ConfigureAwait(false);

    public async Task<LlamaOperationStatusDto> ReconcileLifecycleOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await _db.LocalModelOperations
            .SingleOrDefaultAsync(o => o.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);

        if (operation is null)
        {
            throw new InvalidOperationException($"Operation '{operationId:D}' was not found.");
        }

        var journalSnapshot = await ReconcileOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        return MapStatus(operation, journalSnapshot);
    }

    public async Task ReconcileInFlightLifecycleOperationsAsync(CancellationToken cancellationToken = default)
    {
        var operations = await _db.LocalModelOperations
            .Where(o => o.OperationKind != LocalModelOperationKinds.CuratedInstall
                        && InFlightStatuses.Contains(o.Status))
            .OrderBy(o => o.UpdatedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in operations)
        {
            try
            {
                await ReconcileOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconcile lifecycle operation {OperationId}.", operation.OperationId);
            }
        }
    }

    private async Task<LocalModelOperation> CreateLifecycleOperationAsync(
        string kind,
        string modelId,
        string routerModelId,
        string immutableInputJson,
        bool wasLoadedAtStart,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var sideEffects = new LifecycleSideEffects { WasLoadedAtStart = wasLoadedAtStart };
        var operation = new LocalModelOperation
        {
            OperationId = Guid.NewGuid(),
            OperationKind = kind,
            ModelId = modelId,
            RouterModelId = routerModelId,
            ImmutableInputJson = immutableInputJson,
            Status = "queued",
            CurrentStep = "queued",
            CompletedSideEffectsJson = SerializeSideEffects(sideEffects),
            CreatedUtc = now,
            UpdatedUtc = now,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
        };

        _db.LocalModelOperations.Add(operation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return operation;
    }

    private async Task<OperationJournalSnapshot?> ReconcileOperationAsync(LocalModelOperation operation, CancellationToken cancellationToken)
    {
        if (TerminalStatuses.Contains(operation.Status))
        {
            return null;
        }

        var sideEffects = ParseSideEffects(operation.CompletedSideEffectsJson);
        var alias = operation.RouterModelId ?? string.Empty;

        if (string.Equals(operation.Status, "loadRestore", StringComparison.OrdinalIgnoreCase))
        {
            await TryLoadRestoreAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(operation.Status, "obsoleteCleanup", StringComparison.OrdinalIgnoreCase))
        {
            await TryObsoleteCleanupAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(operation.Status, "provenanceFinalization", StringComparison.OrdinalIgnoreCase))
        {
            await TryProvenanceFinalizationAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(operation.Status, "queued", StringComparison.OrdinalIgnoreCase)
            && !sideEffects.DownloadStarted)
        {
            await BeginQueuedOperationAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var journal = await _adminClient
            .GetDownloadStatusAsync(operation.OperationId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);

        if (journal is null)
        {
            if (sideEffects.DownloadStarted)
            {
                await MarkFailedAsync(
                    operation,
                    "INSTALL_STEP_FAILED",
                    "Llama-admin lost the download operation journal.",
                    "Retry the operation or repair the runtime service.",
                    cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        if (string.Equals(journal.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            await MarkFailedAsync(
                operation,
                journal.Error?.Code ?? "INSTALL_STEP_FAILED",
                journal.ErrorMessage ?? journal.Error?.Message ?? "Download failed.",
                journal.Error?.Remediation ?? "Review the error and retry.",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var journalSnapshot = OperationJournalSnapshot.FromJournal(journal);
        operation.CurrentStep = journal.Status;
        operation.UpdatedUtc = DateTime.UtcNow;

        if (!string.Equals(journal.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            operation.Status = journal.Status;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return journalSnapshot;
        }

        sideEffects.ArtifactsActivated = true;
        sideEffects.AliasRegistered = true;
        operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
        operation.Status = "provenanceFinalization";
        operation.CurrentStep = "provenanceFinalization";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await TryProvenanceFinalizationAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
        return journalSnapshot;
    }

    private async Task BeginQueuedOperationAsync(
        LocalModelOperation operation,
        LifecycleSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        var alias = operation.RouterModelId ?? string.Empty;
        var handle = await _coordinator.AcquireAliasLockAsync(alias, cancellationToken).ConfigureAwait(false);
        await using var _ = handle;

        if (sideEffects.WasLoadedAtStart && !sideEffects.UnloadedForOperation)
        {
            try
            {
                await _llamaClient.UnloadModelAsync(alias, cancellationToken).ConfigureAwait(false);
                sideEffects.UnloadedForOperation = true;
                operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await MarkFailedAsync(
                    operation,
                    LocalModelLifecycleErrorCodes.LoadRestoreFailed,
                    $"Failed to unload alias before lifecycle operation: {ex.Message}",
                    "Unload the alias manually and retry.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var exactRequest = BuildExactDownloadRequest(operation);
        try
        {
            var download = await _adminClient
                .StartExactDownloadAsync(exactRequest, _tokenResolver.Resolve(), cancellationToken)
                .ConfigureAwait(false);

            sideEffects.DownloadStarted = true;
            operation.Status = download.Status;
            operation.CurrentStep = download.Status;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LlamaRuntimeAdminConflictException ex)
        {
            if (!string.Equals(ex.ExistingOperation.OperationId, operation.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                await MarkFailedAsync(
                    operation,
                    "ROUTER_ALIAS_TAKEN",
                    ex.Message,
                    "Wait for the conflicting operation to finish.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            sideEffects.DownloadStarted = true;
            operation.Status = ex.ExistingOperation.Status;
            operation.CurrentStep = ex.ExistingOperation.Status;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryProvenanceFinalizationAsync(
        LocalModelOperation operation,
        LifecycleSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        if (!sideEffects.ArtifactsActivated || !sideEffects.AliasRegistered)
        {
            await MarkFailedAsync(
                operation,
                "INSTALL_STEP_FAILED",
                "Provenance finalization requires completed artifacts and alias registration.",
                "Wait for router registration to complete, then retry.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (string.Equals(operation.OperationKind, LocalModelOperationKinds.ChangeQuant, StringComparison.OrdinalIgnoreCase))
            {
                await CommitChangeQuantProvenanceAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(operation.OperationKind, LocalModelOperationKinds.Repair, StringComparison.OrdinalIgnoreCase))
            {
                await CommitRepairProvenanceAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(operation.OperationKind, LocalModelOperationKinds.CustomInstall, StringComparison.OrdinalIgnoreCase))
            {
                await CommitCustomInstallProvenanceAsync(operation, cancellationToken).ConfigureAwait(false);
            }

            sideEffects.ProvenanceCommitted = true;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.Status = sideEffects.ObsoletePaths.Count > 0 ? "obsoleteCleanup" : "loadRestore";
            operation.CurrentStep = operation.Status;
            operation.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(
                operation,
                CuratedInstallErrorCodes.CatalogFinalization,
                ex.Message,
                "Retry the operation status endpoint to finalize provenance without re-downloading.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation.Status == "obsoleteCleanup")
        {
            await TryObsoleteCleanupAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TryLoadRestoreAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CommitChangeQuantProvenanceAsync(LocalModelOperation operation, CancellationToken cancellationToken)
    {
        var input = ChangeQuantImmutableInput.Deserialize(operation.ImmutableInputJson);
        var installation = await _db.LocalModelInstallations
            .SingleAsync(i => i.ModelId == input.ModelId, cancellationToken)
            .ConfigureAwait(false);

        installation.QuantId = input.NewQuantId;
        installation.QuantLabel = input.NewQuantLabel;
        installation.ResolvedRevision = input.ResolvedRevision;
        installation.ModelArtifactsJson = InstallationArtifactRecords.SerializeFromPaths(
            input.TargetDirectory,
            input.ModelFiles);
        installation.ProjectorArtifactsJson = InstallationArtifactRecords.SerializeFromPaths(
            input.TargetDirectory,
            input.MmprojFiles);
        installation.RouterPresetSnapshotJson = JsonSerializer.Serialize(input.RouterPreset);
        installation.UpdatedUtc = DateTime.UtcNow;

        var sideEffects = ParseSideEffects(operation.CompletedSideEffectsJson);
        sideEffects.ObsoletePaths = input.ObsoleteRepositoryPaths.ToList();
        operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitRepairProvenanceAsync(LocalModelOperation operation, CancellationToken cancellationToken)
    {
        var input = RepairImmutableInput.Deserialize(operation.ImmutableInputJson);
        var installation = await _db.LocalModelInstallations
            .SingleAsync(i => i.ModelId == input.ModelId, cancellationToken)
            .ConfigureAwait(false);

        installation.RouterPresetSnapshotJson = JsonSerializer.Serialize(input.RouterPreset);
        installation.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitCustomInstallProvenanceAsync(LocalModelOperation operation, CancellationToken cancellationToken)
    {
        var input = CustomInstallImmutableInput.Deserialize(operation.ImmutableInputJson);
        var existingModel = await _db.Models.AnyAsync(m => m.ModelId == input.CatalogModelId, cancellationToken).ConfigureAwait(false);
        if (existingModel)
        {
            return;
        }

        var now = DateTime.UtcNow;
        _db.Models.Add(new Model
        {
            ModelId = input.CatalogModelId,
            DisplayName = input.CatalogDisplayName,
            Provider = "llama-cpp",
            Description = input.CatalogDescription,
            ReasoningChoicesJson = input.ReasoningChoicesJson,
            RuntimeConfigJson = LocalRuntimeConfigurationParser.SerializeCanonical(
                new LocalRuntimeConfiguration(input.RouterModelId)),
            CombineSystemAndDeveloperMessages = input.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern = input.ThoughtBlockPattern,
            SamplingParametersJson = input.SamplingParametersJson,
            ThinkingControlJson = input.ThinkingControlJson,
            RequestFieldsWhenToolsPresentJson = input.RequestFieldsWhenToolsPresentJson,
            IsActive = input.CatalogIsActive,
            DisplayOrder = input.CatalogDisplayOrder,
            Created = now,
            Updated = now,
        });

        _db.LocalModelInstallations.Add(new LocalModelInstallation
        {
            ModelId = input.CatalogModelId,
            ManagementMode = "operatorManaged",
            Repository = input.Repository,
            RequestedRevision = input.RequestedRevision,
            ResolvedRevision = input.ResolvedRevision,
            RouterModelId = input.RouterModelId,
            TargetDirectory = input.TargetDirectory,
            ModelArtifactsJson = InstallationArtifactRecords.SerializeFromPaths(input.TargetDirectory, input.ModelFiles),
            ProjectorArtifactsJson = InstallationArtifactRecords.SerializeFromPaths(input.TargetDirectory, input.MmprojFiles),
            RouterPresetSnapshotJson = JsonSerializer.Serialize(input.RouterPreset),
            CreatedUtc = now,
            UpdatedUtc = now,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryObsoleteCleanupAsync(
        LocalModelOperation operation,
        LifecycleSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        if (sideEffects.ObsoleteCleanupCompleted || sideEffects.ObsoletePaths.Count == 0)
        {
            operation.Status = "loadRestore";
            operation.CurrentStep = "loadRestore";
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await TryLoadRestoreAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var input = ChangeQuantImmutableInput.Deserialize(operation.ImmutableInputJson);
            await _adminClient
                .DeleteObsoleteArtifactPathsAsync(input.TargetDirectory, sideEffects.ObsoletePaths, cancellationToken)
                .ConfigureAwait(false);
            sideEffects.ObsoleteCleanupCompleted = true;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.Status = "loadRestore";
            operation.CurrentStep = "loadRestore";
            operation.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await TryLoadRestoreAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(
                operation,
                LocalModelLifecycleErrorCodes.ObsoleteCleanupFailed,
                ex.Message,
                "Artifacts are active; remove obsolete files manually and mark operation complete.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryLoadRestoreAsync(
        LocalModelOperation operation,
        LifecycleSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        if (!sideEffects.WasLoadedAtStart)
        {
            await CompleteOperationAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (sideEffects.LoadRestored)
        {
            await CompleteOperationAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
            return;
        }

        var alias = operation.RouterModelId ?? string.Empty;
        try
        {
            await _llamaClient.LoadModelAsync(alias, cancellationToken).ConfigureAwait(false);
            sideEffects.LoadRestored = true;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            await CompleteOperationAsync(operation, sideEffects, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(
                operation,
                LocalModelLifecycleErrorCodes.LoadRestoreFailed,
                $"Artifacts and provenance are committed but reload failed: {ex.Message}",
                "Load the alias manually from runtime inventory.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteOperationAsync(
        LocalModelOperation operation,
        LifecycleSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        operation.Status = "completed";
        operation.CurrentStep = "completed";
        operation.CompletedUtc = DateTime.UtcNow;
        operation.UpdatedUtc = DateTime.UtcNow;
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.Remediation = null;
        operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ExactStartModelDownloadRequest BuildExactDownloadRequest(LocalModelOperation operation)
    {
        return operation.OperationKind switch
        {
            LocalModelOperationKinds.ChangeQuant => ChangeQuantImmutableInput
                .Deserialize(operation.ImmutableInputJson)
                .ToExactDownloadRequest(operation.OperationId),
            LocalModelOperationKinds.Repair => RepairImmutableInput
                .Deserialize(operation.ImmutableInputJson)
                .ToExactDownloadRequest(operation.OperationId),
            LocalModelOperationKinds.CustomInstall => CustomInstallImmutableInput
                .Deserialize(operation.ImmutableInputJson)
                .ToExactDownloadRequest(operation.OperationId),
            _ => throw new InvalidOperationException($"Unsupported lifecycle operation kind '{operation.OperationKind}'."),
        };
    }

    private async Task MarkFailedAsync(
        LocalModelOperation operation,
        string code,
        string message,
        string remediation,
        CancellationToken cancellationToken)
    {
        operation.Status = "failed";
        operation.CurrentStep = "failed";
        operation.ErrorCode = code;
        operation.ErrorMessage = message;
        operation.Remediation = remediation;
        operation.UpdatedUtc = DateTime.UtcNow;
        operation.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record OperationJournalSnapshot(double? Progress, string? LogLine)
    {
        public static OperationJournalSnapshot FromJournal(ModelDownloadOperationDto journal) =>
            new(journal.Progress, journal.LogLine ?? journal.ErrorMessage);
    }

    private static LlamaOperationStatusDto MapStatus(
        LocalModelOperation operation,
        OperationJournalSnapshot? journalSnapshot = null)
    {
        var sideEffects = ParseSideEffects(operation.CompletedSideEffectsJson);
        AddModelErrorDto? error = null;
        if (!string.IsNullOrWhiteSpace(operation.ErrorCode))
        {
            error = new AddModelErrorDto(
                operation.ErrorCode,
                operation.CurrentStep ?? operation.Status,
                operation.ErrorMessage ?? "Operation failed.",
                operation.Remediation);
        }

        return new LlamaOperationStatusDto(
            OperationId: operation.OperationId.ToString("D"),
            Status: operation.Status,
            Stage: operation.CurrentStep ?? operation.Status,
            RouterModelId: operation.RouterModelId ?? string.Empty,
            Progress: journalSnapshot?.Progress,
            ErrorMessage: operation.ErrorMessage,
            LogLine: journalSnapshot?.LogLine ?? operation.ErrorMessage,
            CompletedSideEffects: new LlamaOperationCompletedSideEffectsDto(
                sideEffects.DownloadStarted,
                sideEffects.ArtifactsActivated,
                sideEffects.AliasRegistered,
                sideEffects.ProvenanceCommitted),
            Error: error,
            InstallationModelId: sideEffects.ProvenanceCommitted ? operation.ModelId : null);
    }

    private static LifecycleSideEffects ParseSideEffects(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LifecycleSideEffects>(json, SideEffectsJsonOptions)
                ?? new LifecycleSideEffects();
        }
        catch (JsonException)
        {
            return new LifecycleSideEffects();
        }
    }

    private static string SerializeSideEffects(LifecycleSideEffects sideEffects) =>
        JsonSerializer.Serialize(sideEffects, SideEffectsJsonOptions);

    private static readonly JsonSerializerOptions SideEffectsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class LifecycleSideEffects
    {
        public bool WasLoadedAtStart { get; set; }
        public bool UnloadedForOperation { get; set; }
        public bool DownloadStarted { get; set; }
        public bool ArtifactsActivated { get; set; }
        public bool AliasRegistered { get; set; }
        public bool ProvenanceCommitted { get; set; }
        public bool ObsoleteCleanupCompleted { get; set; }
        public bool LoadRestored { get; set; }
        public List<string> ObsoletePaths { get; set; } = [];
    }
}
