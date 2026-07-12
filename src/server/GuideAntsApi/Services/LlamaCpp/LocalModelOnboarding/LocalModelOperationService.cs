using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ILocalModelOperationService
{
    Task<LocalModelOperation> CreateCuratedInstallOperationAsync(
        CuratedImmutableOperationInput immutableInput,
        CancellationToken cancellationToken = default);

    Task<LocalModelOperation?> FindActiveByInputHashAsync(
        string inputHash,
        CancellationToken cancellationToken = default);

    Task<LlamaOperationStatusDto?> GetStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<LlamaOperationStatusDto> ReconcileAndGetStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task ReconcileInFlightOperationsAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalModelOperationService : ILocalModelOperationService
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
        "catalogFinalization",
    };

    private readonly ApplicationDbContext _db;
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;
    private readonly ILogger<LocalModelOperationService> _logger;

    public LocalModelOperationService(
        ApplicationDbContext db,
        ILlamaRuntimeAdminClient adminClient,
        IHuggingFaceTokenResolver tokenResolver,
        IRuntimeProfileResolver runtimeProfileResolver,
        ILogger<LocalModelOperationService> logger)
    {
        _db = db;
        _adminClient = adminClient;
        _tokenResolver = tokenResolver;
        _runtimeProfileResolver = runtimeProfileResolver;
        _logger = logger;
    }

    public async Task<LocalModelOperation> CreateCuratedInstallOperationAsync(
        CuratedImmutableOperationInput immutableInput,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var operation = new LocalModelOperation
        {
            OperationId = Guid.NewGuid(),
            OperationKind = "curatedInstall",
            ModelId = immutableInput.CatalogModelId,
            RouterModelId = immutableInput.RouterModelId,
            ImmutableInputJson = immutableInput.ToJson(),
            Status = "queued",
            CurrentStep = "queued",
            CompletedSideEffectsJson = "{}",
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        _db.LocalModelOperations.Add(operation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return operation;
    }

    public async Task<LocalModelOperation?> FindActiveByInputHashAsync(
        string inputHash,
        CancellationToken cancellationToken = default)
    {
        var operations = await _db.LocalModelOperations
            .AsNoTracking()
            .Where(o => o.OperationKind == "curatedInstall" && !TerminalStatuses.Contains(o.Status))
            .OrderByDescending(o => o.CreatedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in operations)
        {
            try
            {
                var input = CuratedImmutableOperationInput.Deserialize(operation.ImmutableInputJson);
                if (string.Equals(input.ComputeHash(), inputHash, StringComparison.Ordinal))
                {
                    return operation;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping operation {OperationId} with invalid immutable input JSON.",
                    operation.OperationId);
            }
        }

        return null;
    }

    public async Task<LlamaOperationStatusDto?> GetStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await _db.LocalModelOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);

        return operation is null ? null : MapStatus(operation);
    }

    public async Task<LlamaOperationStatusDto> ReconcileAndGetStatusAsync(
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

    public async Task ReconcileInFlightOperationsAsync(CancellationToken cancellationToken = default)
    {
        var operations = await _db.LocalModelOperations
            .Where(o => o.OperationKind == "curatedInstall" && InFlightStatuses.Contains(o.Status))
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
                _logger.LogError(
                    ex,
                    "Failed to reconcile curated install operation {OperationId}.",
                    operation.OperationId);
            }
        }
    }

    private async Task<OperationJournalSnapshot?> ReconcileOperationAsync(
        LocalModelOperation operation,
        CancellationToken cancellationToken)
    {
        if (TerminalStatuses.Contains(operation.Status))
        {
            return null;
        }

        var immutableInput = CuratedImmutableOperationInput.Deserialize(operation.ImmutableInputJson);
        var sideEffects = ParseSideEffects(operation.CompletedSideEffectsJson);

        if (string.Equals(operation.Status, "catalogFinalization", StringComparison.OrdinalIgnoreCase))
        {
            await TryFinalizeCatalogAsync(operation, immutableInput, sideEffects, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(operation.Status, "queued", StringComparison.OrdinalIgnoreCase)
            && !sideEffects.DownloadStarted)
        {
            await StartDownloadAsync(operation, immutableInput, sideEffects, cancellationToken).ConfigureAwait(false);
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
                    code: "INSTALL_STEP_FAILED",
                    message: "Llama-admin lost the download operation journal.",
                    remediation: "Retry the install or repair the runtime service.",
                    cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        if (string.Equals(journal.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            await MarkFailedAsync(
                operation,
                code: journal.Error?.Code ?? "INSTALL_STEP_FAILED",
                message: journal.ErrorMessage ?? journal.Error?.Message ?? "Download failed.",
                remediation: journal.Error?.Remediation ?? "Review the error and retry.",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var journalSnapshot = OperationJournalSnapshot.FromJournal(journal);
        operation.CurrentStep = journal.Status;
        operation.UpdatedUtc = DateTime.UtcNow;

        if (string.Equals(journal.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            sideEffects.ArtifactsActivated = true;
            sideEffects.AliasRegistered = true;
            sideEffects.ImmutableInputHash = journal.ImmutableInputHash ?? immutableInput.ComputeHash();
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.Status = "catalogFinalization";
            operation.CurrentStep = "catalogFinalization";
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            operation.Status = journal.Status;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return journalSnapshot;
        }

        await TryFinalizeCatalogAsync(operation, immutableInput, sideEffects, cancellationToken).ConfigureAwait(false);
        return journalSnapshot;
    }

    private async Task StartDownloadAsync(
        LocalModelOperation operation,
        CuratedImmutableOperationInput immutableInput,
        CompletedSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        var hfToken = _tokenResolver.Resolve();
        try
        {
            var download = await _adminClient
                .StartExactDownloadAsync(
                    immutableInput.ToExactDownloadRequest(operation.OperationId),
                    hfToken,
                    cancellationToken)
                .ConfigureAwait(false);

            sideEffects.DownloadStarted = true;
            sideEffects.ImmutableInputHash = download.ImmutableInputHash ?? immutableInput.ComputeHash();
            operation.Status = download.Status;
            operation.CurrentStep = download.Status;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LlamaRuntimeAdminConflictException ex)
        {
            var existing = ex.ExistingOperation;
            if (!string.Equals(existing.OperationId, operation.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                await MarkFailedAsync(
                    operation,
                    code: "ROUTER_ALIAS_TAKEN",
                    message: ex.Message,
                    remediation: "Wait for the conflicting operation to finish or choose another alias.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            sideEffects.DownloadStarted = true;
            operation.Status = existing.Status;
            operation.CurrentStep = existing.Status;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
            operation.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryFinalizeCatalogAsync(
        LocalModelOperation operation,
        CuratedImmutableOperationInput immutableInput,
        CompletedSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        if (!sideEffects.ArtifactsActivated || !sideEffects.AliasRegistered)
        {
            await MarkFailedAsync(
                operation,
                code: "INSTALL_STEP_FAILED",
                message: "Catalog finalization requires completed artifacts and alias registration.",
                remediation: "Wait for router registration to complete, then retry finalization.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var existingModel = await _db.Models
            .AsNoTracking()
            .AnyAsync(m => m.ModelId == immutableInput.CatalogModelId, cancellationToken)
            .ConfigureAwait(false);
        if (existingModel)
        {
            // Reconcile: the catalog row already exists (e.g. a prior text-only install). The download
            // step above fetched any newly-required artifacts (projector) and re-registered the alias.
            // Refresh the installation provenance so it reflects what is actually on disk now.
            await ReconcileExistingInstallationAsync(operation, immutableInput, sideEffects, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var useTransaction = _db.Database.ProviderName is not null
            && !_db.Database.ProviderName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);

        if (useTransaction)
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await CommitFinalizationAsync(operation, immutableInput, sideEffects, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    await MarkFinalizationFailureAsync(operation, ex, cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            return;
        }

        try
        {
            await CommitFinalizationAsync(operation, immutableInput, sideEffects, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await MarkFinalizationFailureAsync(operation, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileExistingInstallationAsync(
        LocalModelOperation operation,
        CuratedImmutableOperationInput immutableInput,
        CompletedSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var installation = await _db.LocalModelInstallations
            .SingleOrDefaultAsync(i => i.ModelId == immutableInput.CatalogModelId, cancellationToken)
            .ConfigureAwait(false);
        if (installation is not null)
        {
            installation.ManagementMode = "curated";
            installation.CatalogId = immutableInput.DefinitionId;
            installation.CatalogVersion = immutableInput.DefinitionVersion;
            installation.Repository = immutableInput.Repository;
            installation.RequestedRevision = immutableInput.RequestedRevision;
            installation.ResolvedRevision = immutableInput.ResolvedRevision;
            installation.QuantId = immutableInput.QuantId;
            installation.QuantLabel = immutableInput.QuantLabel;
            installation.RouterModelId = immutableInput.RouterModelId;
            installation.RuntimeProfileId = immutableInput.RuntimeProfileId;
            installation.TargetDirectory = immutableInput.TargetDirectory;
            installation.ModelArtifactsJson = BuildModelArtifactsJson(immutableInput);
            installation.ProjectorArtifactsJson = BuildProjectorArtifactsJson(immutableInput);
            installation.RouterPresetSnapshotJson = JsonSerializer.Serialize(immutableInput.RouterPreset);
            installation.UpdatedUtc = now;
        }

        operation.Status = "completed";
        operation.CurrentStep = "completed";
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.Remediation = null;
        operation.CompletedUtc = now;
        operation.UpdatedUtc = now;
        sideEffects.CatalogFinalized = true;
        operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitFinalizationAsync(
        LocalModelOperation operation,
        CuratedImmutableOperationInput immutableInput,
        CompletedSideEffects sideEffects,
        CancellationToken cancellationToken)
    {
            var runtimeConfigJson = LocalRuntimeConfigurationParser.SerializeCanonical(
                new LocalRuntimeConfiguration(
                    immutableInput.RouterModelId,
                    immutableInput.RuntimeProfileId));

            var profile = await _runtimeProfileResolver
                .ResolveAsync(immutableInput.RuntimeProfileId, cancellationToken)
                .ConfigureAwait(false);
            var reasoningChoices = profile.ThinkingControl.ChoiceActions.Keys
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .Select(choice => choice.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            string? reasoningChoicesJson = reasoningChoices.Count == 0
                ? null
                : JsonSerializer.Serialize(reasoningChoices);

            var now = DateTime.UtcNow;
            _db.Models.Add(new Model
            {
                ModelId = immutableInput.CatalogModelId,
                DisplayName = immutableInput.CatalogDisplayName,
                Provider = "llama-cpp",
                Description = immutableInput.CatalogDescription,
                ReasoningChoicesJson = reasoningChoicesJson,
                RuntimeConfigJson = runtimeConfigJson,
                IsActive = immutableInput.CatalogIsActive,
                DisplayOrder = immutableInput.CatalogDisplayOrder,
                Created = now,
                Updated = now,
            });

            _db.LocalModelInstallations.Add(new LocalModelInstallation
            {
                ModelId = immutableInput.CatalogModelId,
                ManagementMode = "curated",
                CatalogId = immutableInput.DefinitionId,
                CatalogVersion = immutableInput.DefinitionVersion,
                Repository = immutableInput.Repository,
                RequestedRevision = immutableInput.RequestedRevision,
                ResolvedRevision = immutableInput.ResolvedRevision,
                QuantId = immutableInput.QuantId,
                QuantLabel = immutableInput.QuantLabel,
                RouterModelId = immutableInput.RouterModelId,
                RuntimeProfileId = immutableInput.RuntimeProfileId,
                TargetDirectory = immutableInput.TargetDirectory,
                ModelArtifactsJson = BuildModelArtifactsJson(immutableInput),
                ProjectorArtifactsJson = BuildProjectorArtifactsJson(immutableInput),
                RouterPresetSnapshotJson = JsonSerializer.Serialize(immutableInput.RouterPreset),
                CreatedUtc = now,
                UpdatedUtc = now,
                RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
            });

            operation.Status = "completed";
            operation.CurrentStep = "completed";
            operation.ErrorCode = null;
            operation.ErrorMessage = null;
            operation.Remediation = null;
            operation.CompletedUtc = now;
            operation.UpdatedUtc = now;
            sideEffects.CatalogFinalized = true;
            operation.CompletedSideEffectsJson = SerializeSideEffects(sideEffects);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkFinalizationFailureAsync(
        LocalModelOperation operation,
        Exception ex,
        CancellationToken cancellationToken)
    {
        foreach (var entry in _db.ChangeTracker.Entries<Model>()
                     .Where(e => e.State == EntityState.Added)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in _db.ChangeTracker.Entries<LocalModelInstallation>()
                     .Where(e => e.State == EntityState.Added)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        operation.Status = "catalogFinalization";
        operation.CurrentStep = "catalogFinalization";
        operation.ErrorCode = CuratedInstallErrorCodes.CatalogFinalization;
        operation.ErrorMessage = ex.Message;
        operation.Remediation = "Retry the operation status endpoint to finalize catalog state without re-downloading.";
        operation.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogError(
            ex,
            "Catalog finalization failed for curated install operation {OperationId}.",
            operation.OperationId);
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

    private static string BuildModelArtifactsJson(CuratedImmutableOperationInput input)
    {
        var artifacts = input.ModelFiles.Select(path => new JsonObject
        {
            ["repositoryPath"] = path,
            ["installedRelativePath"] = $"{input.TargetDirectory}/{Path.GetFileName(path)}",
            ["byteSize"] = input.ArtifactMetadata?
                .FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.Ordinal))?.Size,
        }).ToList<JsonNode?>();

        return new JsonArray(artifacts.ToArray()).ToJsonString();
    }

    private static string BuildProjectorArtifactsJson(CuratedImmutableOperationInput input)
    {
        if (input.MmprojFiles.Count == 0)
        {
            return "[]";
        }

        var artifacts = input.MmprojFiles.Select(path => new JsonObject
        {
            ["repositoryPath"] = path,
            ["installedRelativePath"] = $"{input.TargetDirectory}/{Path.GetFileName(path)}",
            ["byteSize"] = input.ArtifactMetadata?
                .FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.Ordinal))?.Size,
        }).ToList<JsonNode?>();

        return new JsonArray(artifacts.ToArray()).ToJsonString();
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
        CuratedImmutableOperationInput? immutableInput = null;
        try
        {
            immutableInput = CuratedImmutableOperationInput.Deserialize(operation.ImmutableInputJson);
        }
        catch
        {
            // Leave immutable summary null when corrupt.
        }

        var sideEffects = ParseSideEffects(operation.CompletedSideEffectsJson);
        SettingsModelDto? catalogModel = null;
        if (string.Equals(operation.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && immutableInput is not null)
        {
            catalogModel = new SettingsModelDto(
                ModelId: immutableInput.CatalogModelId,
                DisplayName: immutableInput.CatalogDisplayName,
                Provider: "llama-cpp",
                Description: immutableInput.CatalogDescription,
                ReasoningChoicesJson: null,
                RuntimeConfigJson: LocalRuntimeConfigurationParser.SerializeCanonical(
                    new LocalRuntimeConfiguration(
                        immutableInput.RouterModelId,
                        immutableInput.RuntimeProfileId)),
                IsActive: immutableInput.CatalogIsActive,
                DisplayOrder: immutableInput.CatalogDisplayOrder,
                Created: operation.CompletedUtc ?? operation.CreatedUtc,
                Updated: operation.CompletedUtc);
        }

        AddModelErrorDto? error = null;
        if (!string.IsNullOrWhiteSpace(operation.ErrorCode))
        {
            error = new AddModelErrorDto(
                Code: operation.ErrorCode,
                Step: operation.CurrentStep ?? operation.Status,
                Message: operation.ErrorMessage ?? "Operation failed.",
                Remediation: operation.Remediation);
        }

        return new LlamaOperationStatusDto(
            OperationId: operation.OperationId.ToString("D"),
            Status: operation.Status,
            Stage: operation.CurrentStep ?? operation.Status,
            RouterModelId: operation.RouterModelId ?? immutableInput?.RouterModelId ?? string.Empty,
            Progress: journalSnapshot?.Progress,
            ErrorMessage: operation.ErrorMessage,
            LogLine: journalSnapshot?.LogLine ?? operation.ErrorMessage,
            ImmutableInputHash: immutableInput?.ComputeHash(),
            ImmutableSummary: immutableInput is null
                ? null
                : new LlamaOperationImmutableSummaryDto(
                    immutableInput.DefinitionId,
                    immutableInput.DefinitionVersion,
                    immutableInput.QuantId,
                    immutableInput.ResolvedRevision),
            CompletedSideEffects: new LlamaOperationCompletedSideEffectsDto(
                sideEffects.DownloadStarted,
                sideEffects.ArtifactsActivated,
                sideEffects.AliasRegistered,
                sideEffects.CatalogFinalized),
            Error: error,
            CatalogModel: catalogModel,
            InstallationModelId: sideEffects.CatalogFinalized ? immutableInput?.CatalogModelId : null);
    }

    private static CompletedSideEffects ParseSideEffects(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CompletedSideEffects>(json, SideEffectsJsonOptions)
                ?? new CompletedSideEffects();
        }
        catch (JsonException)
        {
            return new CompletedSideEffects();
        }
    }

    private static string SerializeSideEffects(CompletedSideEffects sideEffects) =>
        JsonSerializer.Serialize(sideEffects, SideEffectsJsonOptions);

    private static readonly JsonSerializerOptions SideEffectsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class CompletedSideEffects
    {
        public bool DownloadStarted { get; set; }
        public bool ArtifactsActivated { get; set; }
        public bool AliasRegistered { get; set; }
        public bool CatalogFinalized { get; set; }
        public string? ImmutableInputHash { get; set; }
    }
}
