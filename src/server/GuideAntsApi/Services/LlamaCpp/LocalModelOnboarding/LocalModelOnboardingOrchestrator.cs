using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ILocalModelOnboardingOrchestrator
{
    Task<LocalModelOnboardingResult> OnboardAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default);

    Task<ModelDownloadOperationDto?> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<LlamaOperationStatusDto?> GetCuratedOperationStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public sealed class LocalModelOnboardingOrchestrator : ILocalModelOnboardingOrchestrator
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly IHuggingFaceModelDownloadService _downloadService;
    private readonly ICuratedInstallResolver _curatedInstallResolver;
    private readonly ILocalModelOperationService _operationService;
    private readonly ICustomInstallResolver _customInstallResolver;
    private readonly ILocalModelLifecycleOperationService _lifecycleOperationService;
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly ApplicationDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocalModelOnboardingOrchestrator> _logger;

    public LocalModelOnboardingOrchestrator(
        IApplicationSettingsService settingsService,
        IHuggingFaceModelDownloadService downloadService,
        ICuratedInstallResolver curatedInstallResolver,
        ILocalModelOperationService operationService,
        ICustomInstallResolver customInstallResolver,
        ILocalModelLifecycleOperationService lifecycleOperationService,
        ILlamaRuntimeAdminClient adminClient,
        ApplicationDbContext db,
        IServiceScopeFactory scopeFactory,
        ILogger<LocalModelOnboardingOrchestrator> logger)
    {
        _settingsService = settingsService;
        _downloadService = downloadService;
        _curatedInstallResolver = curatedInstallResolver;
        _operationService = operationService;
        _customInstallResolver = customInstallResolver;
        _lifecycleOperationService = lifecycleOperationService;
        _adminClient = adminClient;
        _db = db;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<LocalModelOnboardingResult> OnboardAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(command.InstallSource, LocalModelInstallSources.Curated, StringComparison.OrdinalIgnoreCase))
        {
            return await OnboardCuratedAsync(request, command, cancellationToken).ConfigureAwait(false);
        }

        if (command.ExplicitHuggingFace is not null)
        {
            return await OnboardCustomExplicitAsync(request, command, cancellationToken).ConfigureAwait(false);
        }

        return await OnboardLegacyAsync(request, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalModelOnboardingResult> OnboardCuratedAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        var immutableInput = await _curatedInstallResolver
            .ResolveAsync(request, command, cancellationToken)
            .ConfigureAwait(false);
        var inputHash = immutableInput.ComputeHash();

        var existing = await _operationService
            .FindActiveByInputHashAsync(inputHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Reusing in-flight curated install operation {OperationId} for model {ModelId}.",
                existing.OperationId,
                immutableInput.CatalogModelId);

            var reusedOperationId = existing.OperationId;
            QueueBackgroundWork(async (services, cancellationToken) =>
            {
                var operationService = services.GetRequiredService<ILocalModelOperationService>();
                await operationService
                    .ReconcileAndGetStatusAsync(reusedOperationId, cancellationToken)
                    .ConfigureAwait(false);
            }, "Background curated install reconciliation failed for reused operation {OperationId}.", reusedOperationId);

            return new LocalModelOnboardingResult(
                OperationId: existing.OperationId.ToString("D"),
                AddOperation: new AddModelOperationDto(
                    Kind: "async",
                    CatalogModel: null,
                    Status: "inProgress",
                    Error: null));
        }

        var operation = await _operationService
            .CreateCuratedInstallOperationAsync(immutableInput, cancellationToken)
            .ConfigureAwait(false);

        var operationId = operation.OperationId;
        QueueBackgroundWork(async (services, cancellationToken) =>
        {
            var operationService = services.GetRequiredService<ILocalModelOperationService>();
            await operationService
                .ReconcileAndGetStatusAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
        }, "Background curated install reconciliation failed for operation {OperationId}.", operationId);

        return new LocalModelOnboardingResult(
            OperationId: operation.OperationId.ToString("D"),
            AddOperation: new AddModelOperationDto(
                Kind: "async",
                CatalogModel: null,
                Status: "inProgress",
                Error: null));
    }

    private async Task<LocalModelOnboardingResult> OnboardLegacyAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        var localRuntimeJson = BuildLlamaLocalRuntimeJson(command);
        var profile = command.ToRowOwnedRuntimeProfileData();
        var reasoningChoicesJson = command.HasProviderConfigChatBehavior
            ? command.ReasoningChoicesJson
            : ModelChatBehavior.DeriveReasoningChoicesJson(
                System.Text.Json.JsonSerializer.Serialize(profile.ThinkingControl));

        var createRequest = ModelChatBehavior.BuildCreateRequest(
            request.Catalog,
            request.Provider.Trim(),
            reasoningChoicesJson,
            localRuntimeJson,
            profile);

        if (string.Equals(command.InstallSource, LocalModelInstallSources.ExistingAlias, StringComparison.OrdinalIgnoreCase))
        {
            var routerEntries = await _adminClient.GetRouterEntriesAsync(cancellationToken).ConfigureAwait(false);
            var entry = routerEntries.Entries.FirstOrDefault(e =>
                string.Equals(e.Alias, command.RouterModelId, StringComparison.Ordinal));
            if (entry is null)
            {
                throw new InvalidOperationException($"Router alias '{command.RouterModelId}' was not found.");
            }

            var attached = await _settingsService.CreateModelAsync(createRequest, cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            _db.LocalModelInstallations.Add(new LocalModelInstallation
            {
                ModelId = attached.ModelId,
                ManagementMode = "operatorManaged",
                RouterModelId = command.RouterModelId,
                ModelArtifactsJson = InstallationArtifactRecords.Serialize(
                [
                    new InstallationArtifactDto(
                        RepositoryPath: Path.GetFileName(entry.ModelPath ?? string.Empty),
                        InstalledRelativePath: entry.ModelPath ?? string.Empty),
                ]),
                ProjectorArtifactsJson = string.IsNullOrWhiteSpace(entry.MmprojPath)
                    ? "[]"
                    : InstallationArtifactRecords.Serialize(
                    [
                        new InstallationArtifactDto(
                            RepositoryPath: Path.GetFileName(entry.MmprojPath),
                            InstalledRelativePath: entry.MmprojPath),
                    ]),
                RouterPresetSnapshotJson = System.Text.Json.JsonSerializer.Serialize(entry.Preset ?? new Dictionary<string, string>()),
                CreatedUtc = now,
                UpdatedUtc = now,
                RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new LocalModelOnboardingResult(
                OperationId: null,
                AddOperation: new AddModelOperationDto(
                    Kind: "sync",
                    CatalogModel: attached,
                    Status: "completed",
                    Error: null));
        }

        ModelDownloadOperationDto op;
        try
        {
            op = await _downloadService.StartDownloadAsync(
                BuildStartModelDownloadRequest(command),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LlamaRuntimeAdminConflictException ex)
        {
            op = ex.ExistingOperation;
            _logger.LogInformation(
                "Reusing in-flight local onboarding operation {OperationId} for alias {Alias}.",
                LogValueSanitizer.Sanitize(op.OperationId),
                LogValueSanitizer.Sanitize(command.RouterModelId));
        }

        return new LocalModelOnboardingResult(
            OperationId: op.OperationId,
            AddOperation: new AddModelOperationDto(
                Kind: "async",
                CatalogModel: null,
                Status: "inProgress",
                Error: null));
    }

    public async Task<ModelDownloadOperationDto?> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (Guid.TryParse(operationId, out var operationGuid))
        {
            var kind = await LoadOperationKindAsync(operationGuid, cancellationToken).ConfigureAwait(false);

            if (IsCuratedInstall(kind))
            {
                var curated = await _operationService
                    .GetStatusAsync(operationGuid, cancellationToken)
                    .ConfigureAwait(false);
                if (curated is not null)
                {
                    return MapCuratedToLegacyDownloadDto(curated);
                }
            }
            else if (kind is not null)
            {
                var lifecycleStatus = await _lifecycleOperationService
                    .ReconcileLifecycleOperationAsync(operationGuid, cancellationToken)
                    .ConfigureAwait(false);
                return MapCuratedToLegacyDownloadDto(lifecycleStatus);
            }
        }

        return await _downloadService.GetOperationStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LlamaOperationStatusDto?> GetCuratedOperationStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var kind = await LoadOperationKindAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (kind is null)
        {
            return null;
        }

        return IsCuratedInstall(kind)
            ? await _operationService
                .ReconcileAndGetStatusAsync(operationId, cancellationToken)
                .ConfigureAwait(false)
            : await _lifecycleOperationService
                .ReconcileLifecycleOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Selects the state machine that owns an operation. Dispatch must key off
    /// <see cref="LocalModelOperation.OperationKind"/>: the curated lookup matches rows of
    /// every kind, so probing it first sent lifecycle operations (change quant, repair,
    /// custom install) into the curated-install state machine, which stamped them with a
    /// curated-only status that no lifecycle sweep could advance and that blocked the
    /// alias permanently.
    /// </summary>
    private async Task<string?> LoadOperationKindAsync(Guid operationId, CancellationToken cancellationToken) =>
        await _db.LocalModelOperations
            .AsNoTracking()
            .Where(o => o.OperationId == operationId)
            .Select(o => o.OperationKind)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static bool IsCuratedInstall(string? kind) =>
        string.Equals(kind, LocalModelOperationKinds.CuratedInstall, StringComparison.Ordinal);

    private async Task<LocalModelOnboardingResult> OnboardCustomExplicitAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        var immutableInput = await _customInstallResolver
            .ResolveAsync(request, command, cancellationToken)
            .ConfigureAwait(false);

        var operation = await _lifecycleOperationService
            .CreateCustomInstallOperationAsync(immutableInput, cancellationToken)
            .ConfigureAwait(false);

        var operationId = operation.OperationId;
        QueueBackgroundWork(async (services, cancellationToken) =>
        {
            var lifecycleOperationService = services.GetRequiredService<ILocalModelLifecycleOperationService>();
            await lifecycleOperationService
                .ReconcileLifecycleOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
        }, "Background custom install reconciliation failed for operation {OperationId}.", operationId);

        return new LocalModelOnboardingResult(
            OperationId: operation.OperationId.ToString("D"),
            AddOperation: new AddModelOperationDto(
                Kind: "async",
                CatalogModel: null,
                Status: "inProgress",
                Error: null));
    }

    private static ModelDownloadOperationDto MapCuratedToLegacyDownloadDto(LlamaOperationStatusDto status) =>
        new(
            OperationId: status.OperationId,
            Status: status.Status,
            RouterModelId: status.RouterModelId,
            Progress: status.Progress,
            ErrorMessage: status.ErrorMessage,
            LogLine: status.LogLine,
            ImmutableInputHash: status.ImmutableInputHash,
            Journal: status.Journal,
            Error: status.Error);

    public static string BuildLlamaLocalRuntimeJson(LocalModelOnboardingCommand command)
    {
        var config = new LocalRuntimeConfiguration(RouterModelId: command.RouterModelId);

        return LocalRuntimeConfigurationParser.SerializeCanonical(config);
    }

    private static StartModelDownloadRequest BuildStartModelDownloadRequest(LocalModelOnboardingCommand command)
    {
        return new StartModelDownloadRequest(
            Repository: command.Repository!,
            QuantIncludePattern: command.QuantIncludePattern!,
            MmprojIncludePattern: command.MmprojIncludePattern ?? string.Empty,
            RouterModelId: command.RouterModelId,
            TargetDirectory: command.TargetDirectory!,
            CatalogModelId: command.CatalogModelId,
            CatalogDisplayName: command.CatalogDisplayName,
            CatalogDescription: command.CatalogDescription,
            CatalogIsActive: command.CatalogIsActive,
            CatalogDisplayOrder: command.CatalogDisplayOrder,
            CatalogLoadParamsJson: System.Text.Json.JsonSerializer.Serialize(new { model = command.RouterModelId }),
            CatalogParallelToolCalls: false,
            CatalogRouterContextSize: command.RouterContextSize,
            CatalogRouterCacheRamMib: command.RouterCacheRamMib);
    }

    private void QueueBackgroundWork(
        Func<IServiceProvider, CancellationToken, Task> work,
        string failureMessageTemplate,
        Guid operationId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await work(scope.ServiceProvider, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, failureMessageTemplate, operationId);
            }
        }, CancellationToken.None);
    }
}
