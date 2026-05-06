using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.LlamaCpp;

public interface IHuggingFaceModelDownloadService
{
    Task<ModelDownloadOperationDto> StartDownloadAsync(StartModelDownloadRequest request, CancellationToken cancellationToken = default);

    Task<ModelDownloadOperationDto?> GetOperationStatusAsync(string operationId, CancellationToken cancellationToken = default);

    Task<SettingsModelDto> AttachExistingAliasAsync(
        CreateSettingsModelRequest createRequest,
        string routerModelId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// API-side adapter: delegates Hugging Face download + router registration work
/// to the guideants-ai runtime admin service, so the web API never requires
/// direct access to model-storage volumes.
/// </summary>
public sealed class HuggingFaceModelDownloadService : IHuggingFaceModelDownloadService
{
    private static readonly ConcurrentDictionary<string, PendingCatalogRegistration> PendingCatalogRegistrations = new();

    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;
    private readonly IOptionsMonitor<LlamaModelManagementOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HuggingFaceModelDownloadService> _logger;

    public HuggingFaceModelDownloadService(
        ILlamaRuntimeAdminClient adminClient,
        IHuggingFaceTokenResolver tokenResolver,
        IRuntimeProfileResolver runtimeProfileResolver,
        IOptionsMonitor<LlamaModelManagementOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<HuggingFaceModelDownloadService> logger)
    {
        _adminClient = adminClient;
        _tokenResolver = tokenResolver;
        _runtimeProfileResolver = runtimeProfileResolver;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ModelDownloadOperationDto> StartDownloadAsync(
        StartModelDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var options = _options.CurrentValue;
        var resolvedHfToken = _tokenResolver.Resolve();
        var pendingRegistration = await TryBuildCatalogRegistrationAsync(request, cancellationToken).ConfigureAwait(false);

        ModelDownloadOperationDto op;
        try
        {
            op = await _adminClient
                .StartDownloadAsync(
                    request,
                    resolvedHfToken: resolvedHfToken,
                    allowOverwrite: options.AllowOverwrite,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start delegated llama download for alias {Alias} from repo {Repo}.",
                request.RouterModelId,
                request.Repository);
            throw;
        }

        if (pendingRegistration is not null && !string.IsNullOrWhiteSpace(op.OperationId))
        {
            PendingCatalogRegistrations[op.OperationId] = pendingRegistration;
        }

        return op;
    }

    public async Task<ModelDownloadOperationDto?> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var op = await _adminClient.GetDownloadStatusAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (op is null)
        {
            return null;
        }

        if (string.Equals(op.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            PendingCatalogRegistrations.TryRemove(operationId, out _);
            return op;
        }

        if (string.Equals(op.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && PendingCatalogRegistrations.TryRemove(operationId, out var pendingRegistration))
        {
            var created = await TryRegisterCatalogAsync(pendingRegistration, cancellationToken)
                .ConfigureAwait(false);
            if (created.Succeeded)
            {
                return op with
                {
                    Status = "completed",
                    ErrorMessage = null,
                    Error = null,
                    LogLine = "Completed.",
                };
            }

            return op with
            {
                Status = "failed",
                ErrorMessage = created.Error?.Message,
                Error = created.Error,
                LogLine = created.Error?.Message ?? op.LogLine,
            };
        }

        return op;
    }

    public async Task<SettingsModelDto> AttachExistingAliasAsync(
        CreateSettingsModelRequest createRequest,
        string routerModelId,
        CancellationToken cancellationToken = default)
    {
        var alias = (routerModelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: "Router alias is required.",
                remediation: "Pick an existing orphaned alias in Step 3.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var inventoryService = scope.ServiceProvider.GetRequiredService<ILlamaRuntimeInventoryService>();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();

        var inventory = await inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var row = inventory.FirstOrDefault(item => string.Equals(item.RouterModelId, alias, StringComparison.Ordinal));
        if (row is null)
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: $"Router alias '{alias}' does not exist.",
                remediation: "Back up and pick a live alias from Runtime Inventory.");
        }

        if (!row.HasModelFile || !row.HasMmprojFile)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: $"Router alias '{alias}' is missing one or more model artifacts on disk.",
                remediation: "Repair the alias in Local Llama Runtime or re-install it from Hugging Face.");
        }

        if (row.CatalogModelIds.Count > 0)
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: $"Router alias '{alias}' is already adopted by catalog rows: {string.Join(", ", row.CatalogModelIds)}.",
                remediation: "Pick an orphaned alias or delete the existing catalog row first.");
        }

        return await settings.CreateModelAsync(createRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CreateResult> TryRegisterCatalogAsync(
        PendingCatalogRegistration pendingRegistration,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
            var createRequest = pendingRegistration.CreateRequest;

            var existing = await settings.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Any(m => string.Equals(m.ModelId, createRequest.ModelId, StringComparison.Ordinal)))
            {
                _logger.LogInformation(
                    "Catalog model '{ModelId}' already exists; skipping auto-registration for router alias '{Alias}'.",
                    createRequest.ModelId,
                    pendingRegistration.RouterModelId);
                return CreateResult.Success();
            }

            await settings.CreateModelAsync(createRequest, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Auto-registered catalog model '{ModelId}' (router alias '{Alias}', profile '{Profile}') after download completion.",
                createRequest.ModelId,
                pendingRegistration.RouterModelId,
                pendingRegistration.RuntimeProfileId);
            return CreateResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to auto-register catalog model '{ModelId}' for router alias '{Alias}'. Operator may need to create it manually.",
                pendingRegistration.CreateRequest.ModelId,
                pendingRegistration.RouterModelId);
            return CreateResult.Failure(new AddModelErrorDto(
                Code: "INSTALL_STEP_FAILED",
                Step: "catalog",
                Message: ex.Message,
                Remediation: "Fix the catalog or runtime profile issue, then retry from the failed step."));
        }
    }

    private async Task<string?> DeriveReasoningChoicesJsonAsync(string runtimeProfileId, CancellationToken cancellationToken)
    {
        var profile = await _runtimeProfileResolver.ResolveAsync(runtimeProfileId, cancellationToken).ConfigureAwait(false);
        var choices = profile.ThinkingControl.ChoiceActions.Keys
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => choice.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return choices.Count == 0 ? null : JsonSerializer.Serialize(choices);
    }

    private async Task<PendingCatalogRegistration?> TryBuildCatalogRegistrationAsync(
        StartModelDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var modelId = request.CatalogModelId?.Trim();
        var displayName = request.CatalogDisplayName?.Trim();
        var profileId = request.CatalogRuntimeProfileId?.Trim();

        if (string.IsNullOrWhiteSpace(modelId)
            || string.IsNullOrWhiteSpace(displayName)
            || string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var localRuntimeJson = BuildRuntimeConfigJson(request);
        var reasoningChoicesJson = await DeriveReasoningChoicesJsonAsync(profileId, cancellationToken).ConfigureAwait(false);
        var createRequest = new CreateSettingsModelRequest(
            ModelId: modelId,
            DisplayName: displayName,
            Provider: "llama-cpp",
            Description: string.IsNullOrWhiteSpace(request.CatalogDescription)
                ? null
                : request.CatalogDescription.Trim(),
            ReasoningChoicesJson: reasoningChoicesJson,
            RuntimeConfigJson: localRuntimeJson,
            IsActive: request.CatalogIsActive ?? true,
            DisplayOrder: request.CatalogDisplayOrder);

        return new PendingCatalogRegistration(
            createRequest,
            RouterModelId: request.RouterModelId.Trim(),
            RuntimeProfileId: profileId);
    }

    private static string BuildRuntimeConfigJson(StartModelDownloadRequest request)
    {
        JsonObject? loadParams = null;
        if (!string.IsNullOrWhiteSpace(request.CatalogLoadParamsJson))
        {
            var parsed = JsonNode.Parse(request.CatalogLoadParamsJson);
            if (parsed is JsonObject parsedObject)
            {
                loadParams = parsedObject;
            }
        }

        loadParams ??= new JsonObject
        {
            ["model"] = request.RouterModelId.Trim(),
        };

        var config = new LocalRuntimeConfiguration(
            RouterModelId: request.RouterModelId.Trim(),
            RuntimeProfileId: request.CatalogRuntimeProfileId!.Trim(),
            LoadParams: loadParams,
            ParallelToolCalls: request.CatalogParallelToolCalls ?? false,
            RouterContextSize: request.CatalogRouterContextSize,
            RouterCacheRamMib: request.CatalogRouterCacheRamMib);

        return LocalRuntimeConfigurationParser.SerializeCanonical(config);
    }

    private static void ValidateRequest(StartModelDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            throw new ArgumentException("Repository is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.QuantIncludePattern))
        {
            throw new ArgumentException("Quant include pattern is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.MmprojIncludePattern))
        {
            throw new ArgumentException("mmproj include pattern is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RouterModelId))
        {
            throw new ArgumentException("Router model id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.TargetDirectory))
        {
            throw new ArgumentException("Target directory is required.", nameof(request));
        }
    }

    private sealed record PendingCatalogRegistration(
        CreateSettingsModelRequest CreateRequest,
        string RouterModelId,
        string RuntimeProfileId);

    private sealed record CreateResult(
        bool Succeeded,
        AddModelErrorDto? Error)
    {
        public static CreateResult Success() => new(true, null);

        public static CreateResult Failure(AddModelErrorDto error) => new(false, error);
    }
}
