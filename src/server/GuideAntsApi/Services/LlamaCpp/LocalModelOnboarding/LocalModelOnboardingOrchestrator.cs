using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;

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
}

public sealed class LocalModelOnboardingOrchestrator : ILocalModelOnboardingOrchestrator
{
    private static readonly ConcurrentDictionary<string, PendingCatalogRegistration> PendingCatalogRegistrations = new();

    private readonly IApplicationSettingsService _settingsService;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;
    private readonly IHuggingFaceModelDownloadService _downloadService;
    private readonly ILogger<LocalModelOnboardingOrchestrator> _logger;

    public LocalModelOnboardingOrchestrator(
        IApplicationSettingsService settingsService,
        IRuntimeProfileResolver runtimeProfileResolver,
        IHuggingFaceModelDownloadService downloadService,
        ILogger<LocalModelOnboardingOrchestrator> logger)
    {
        _settingsService = settingsService;
        _runtimeProfileResolver = runtimeProfileResolver;
        _downloadService = downloadService;
        _logger = logger;
    }

    public async Task<LocalModelOnboardingResult> OnboardAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        var localRuntimeJson = BuildLlamaLocalRuntimeJson(command);
        var reasoningChoicesJson = await DeriveReasoningChoicesJsonAsync(command.RuntimeProfileId, cancellationToken)
            .ConfigureAwait(false);

        var createRequest = BuildModelCreateRequest(
            request,
            reasoningChoicesJson,
            localRuntimeJson);

        if (string.Equals(command.InstallSource, LocalModelInstallSources.ExistingAlias, StringComparison.OrdinalIgnoreCase))
        {
            var attached = await _settingsService.CreateModelAsync(createRequest, cancellationToken).ConfigureAwait(false);
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
            // Conflict translation is transport-level; dedupe/reuse policy is domain-level.
            op = ex.ExistingOperation;
            _logger.LogInformation(
                "Reusing in-flight local onboarding operation {OperationId} for alias {Alias}.",
                op.OperationId,
                command.RouterModelId);
        }

        if (!string.IsNullOrWhiteSpace(op.OperationId))
        {
            PendingCatalogRegistrations[op.OperationId] = new PendingCatalogRegistration(
                createRequest,
                command.RouterModelId,
                command.RuntimeProfileId);
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
        var op = await _downloadService.GetOperationStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (op is null)
        {
            return null;
        }

        if (string.Equals(op.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            PendingCatalogRegistrations.TryRemove(operationId, out _);
            return op;
        }

        if (!string.Equals(op.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return op;
        }

        if (!PendingCatalogRegistrations.TryRemove(operationId, out var pendingRegistration))
        {
            return op;
        }

        try
        {
            var existing = await _settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Any(m => string.Equals(m.ModelId, pendingRegistration.CreateRequest.ModelId, StringComparison.Ordinal)))
            {
                _logger.LogInformation(
                    "Catalog model '{ModelId}' already exists; skipping auto-registration for router alias '{Alias}'.",
                    pendingRegistration.CreateRequest.ModelId,
                    pendingRegistration.RouterModelId);
                return op;
            }

            await _settingsService.CreateModelAsync(pendingRegistration.CreateRequest, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Auto-registered catalog model '{ModelId}' (router alias '{Alias}', profile '{Profile}') after download completion.",
                pendingRegistration.CreateRequest.ModelId,
                pendingRegistration.RouterModelId,
                pendingRegistration.RuntimeProfileId);
            return op;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to auto-register catalog model '{ModelId}' for router alias '{Alias}'. Operator may need to create it manually.",
                pendingRegistration.CreateRequest.ModelId,
                pendingRegistration.RouterModelId);
            return op with
            {
                Status = "failed",
                ErrorMessage = ex.Message,
                Error = new AddModelErrorDto(
                    Code: "INSTALL_STEP_FAILED",
                    Step: "catalog",
                    Message: ex.Message,
                    Remediation: "Fix the catalog or runtime profile issue, then retry from the failed step."),
                LogLine = ex.Message,
            };
        }
    }

    public static string BuildLlamaLocalRuntimeJson(LocalModelOnboardingCommand command)
    {
        var config = new LocalRuntimeConfiguration(
            RouterModelId: command.RouterModelId,
            RuntimeProfileId: command.RuntimeProfileId,
            LoadParams: new JsonObject
            {
                ["model"] = command.RouterModelId,
            },
            ParallelToolCalls: false,
            RouterContextSize: command.RouterContextSize,
            RouterCacheRamMib: command.RouterCacheRamMib);

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
            CatalogRuntimeProfileId: command.RuntimeProfileId,
            CatalogDescription: command.CatalogDescription,
            CatalogIsActive: command.CatalogIsActive,
            CatalogDisplayOrder: command.CatalogDisplayOrder,
            CatalogLoadParamsJson: JsonSerializer.Serialize(new { model = command.RouterModelId }),
            CatalogParallelToolCalls: false,
            CatalogRouterContextSize: command.RouterContextSize,
            CatalogRouterCacheRamMib: command.RouterCacheRamMib);
    }

    private static CreateSettingsModelRequest BuildModelCreateRequest(
        AddModelRequest request,
        string? reasoningChoicesJson,
        string? runtimeConfigJson)
    {
        return new CreateSettingsModelRequest(
            ModelId: request.Catalog.ModelId.Trim(),
            DisplayName: request.Catalog.DisplayName.Trim(),
            Provider: request.Provider.Trim(),
            Description: string.IsNullOrWhiteSpace(request.Catalog.Description)
                ? null
                : request.Catalog.Description.Trim(),
            ReasoningChoicesJson: reasoningChoicesJson,
            RuntimeConfigJson: runtimeConfigJson,
            IsActive: request.Catalog.IsActive,
            DisplayOrder: request.Catalog.DisplayOrder);
    }

    private async Task<string?> DeriveReasoningChoicesJsonAsync(string runtimeProfileId, CancellationToken cancellationToken)
    {
        var profile = await _runtimeProfileResolver.ResolveAsync(runtimeProfileId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        var choices = profile.ThinkingControl.ChoiceActions.Keys
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => choice.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return choices.Count == 0 ? null : JsonSerializer.Serialize(choices);
    }

    private sealed record PendingCatalogRegistration(
        CreateSettingsModelRequest CreateRequest,
        string RouterModelId,
        string RuntimeProfileId);
}
