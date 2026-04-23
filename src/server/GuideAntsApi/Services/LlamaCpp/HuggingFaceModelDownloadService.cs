using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
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

        var intent = TryBuildIntent(request);
        if (intent is not null && !string.IsNullOrWhiteSpace(op.OperationId))
        {
            await PersistIntentAsync(op.OperationId, intent, cancellationToken).ConfigureAwait(false);
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

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedIntent = await db.LlamaCatalogDownloadIntents
            .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(op.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            if (persistedIntent is not null)
            {
                db.LlamaCatalogDownloadIntents.Remove(persistedIntent);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return op;
        }

        if (string.Equals(op.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && persistedIntent is not null)
        {
            if (!persistedIntent.RegisteringCatalogObserved)
            {
                persistedIntent.RegisteringCatalogObserved = true;
                persistedIntent.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return op with
                {
                    Status = "registeringCatalog",
                    ErrorMessage = null,
                    Error = null,
                    LogLine = "Registering catalog entry.",
                };
            }

            var created = await TryRealizeIntentAsync(MapToCatalogIntent(persistedIntent), cancellationToken)
                .ConfigureAwait(false);
            if (created.Succeeded)
            {
                db.LlamaCatalogDownloadIntents.Remove(persistedIntent);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return op with
                {
                    Status = "completed",
                    ErrorMessage = null,
                    Error = null,
                    LogLine = "Completed.",
                };
            }

            persistedIntent.LastErrorCode = created.Error?.Code;
            persistedIntent.LastErrorStep = created.Error?.Step;
            persistedIntent.LastErrorMessage = created.Error?.Message;
            persistedIntent.LastErrorRemediation = created.Error?.Remediation;
            persistedIntent.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task PersistIntentAsync(string operationId, CatalogIntent intent, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var existing = await db.LlamaCatalogDownloadIntents
            .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.LlamaCatalogDownloadIntents.Add(new LlamaCatalogDownloadIntent
            {
                OperationId = operationId,
                ModelId = intent.ModelId,
                DisplayName = intent.DisplayName,
                RuntimeProfileId = intent.RuntimeProfileId,
                RouterModelId = intent.RouterModelId,
                Description = intent.Description,
                ResourceGroupKey = intent.ResourceGroupKey,
                IsActive = intent.IsActive,
                DisplayOrder = intent.DisplayOrder,
                LoadParamsJson = intent.LoadParamsJson,
                ParallelToolCalls = intent.ParallelToolCalls,
                RouterContextSize = intent.RouterContextSize,
                RouterCacheRamMib = intent.RouterCacheRamMib,
                RegisteringCatalogObserved = false,
                LastErrorCode = null,
                LastErrorStep = null,
                LastErrorMessage = null,
                LastErrorRemediation = null,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.ModelId = intent.ModelId;
            existing.DisplayName = intent.DisplayName;
            existing.RuntimeProfileId = intent.RuntimeProfileId;
            existing.RouterModelId = intent.RouterModelId;
            existing.Description = intent.Description;
            existing.ResourceGroupKey = intent.ResourceGroupKey;
            existing.IsActive = intent.IsActive;
            existing.DisplayOrder = intent.DisplayOrder;
            existing.LoadParamsJson = intent.LoadParamsJson;
            existing.ParallelToolCalls = intent.ParallelToolCalls;
            existing.RouterContextSize = intent.RouterContextSize;
            existing.RouterCacheRamMib = intent.RouterCacheRamMib;
            existing.RegisteringCatalogObserved = false;
            existing.LastErrorCode = null;
            existing.LastErrorStep = null;
            existing.LastErrorMessage = null;
            existing.LastErrorRemediation = null;
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<CreateResult> TryRealizeIntentAsync(CatalogIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();

            var existing = await settings.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Any(m => string.Equals(m.ModelId, intent.ModelId, StringComparison.Ordinal)))
            {
                _logger.LogInformation(
                    "Catalog model '{ModelId}' already exists; skipping auto-registration for router alias '{Alias}'.",
                    intent.ModelId,
                    intent.RouterModelId);
                return CreateResult.Success();
            }

            var localRuntimeJson = BuildLocalRuntimeJson(intent);
            var reasoningChoicesJson = await DeriveReasoningChoicesJsonAsync(intent.RuntimeProfileId, cancellationToken)
                .ConfigureAwait(false);

            var createRequest = new CreateSettingsModelRequest(
                ModelId: intent.ModelId,
                DisplayName: intent.DisplayName,
                Provider: "llama-cpp",
                Description: intent.Description,
                ReasoningChoicesJson: reasoningChoicesJson,
                LocalRuntimeJson: localRuntimeJson,
                IsActive: intent.IsActive,
                DisplayOrder: intent.DisplayOrder);

            await settings.CreateModelAsync(createRequest, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Auto-registered catalog model '{ModelId}' (router alias '{Alias}', profile '{Profile}') after download completion.",
                intent.ModelId,
                intent.RouterModelId,
                intent.RuntimeProfileId);
            return CreateResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to auto-register catalog model '{ModelId}' for router alias '{Alias}'. Operator may need to create it manually.",
                intent.ModelId,
                intent.RouterModelId);
            return CreateResult.Failure(new AddModelErrorDto(
                Code: "INSTALL_STEP_FAILED",
                Step: "registeringCatalog",
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

    private static string BuildLocalRuntimeJson(CatalogIntent intent)
    {
        JsonObject? loadParams = null;
        if (!string.IsNullOrWhiteSpace(intent.LoadParamsJson))
        {
            var parsed = JsonNode.Parse(intent.LoadParamsJson);
            if (parsed is JsonObject parsedObject)
            {
                loadParams = parsedObject;
            }
        }

        loadParams ??= new JsonObject
        {
            ["model"] = intent.RouterModelId,
        };

        var config = new LocalRuntimeConfiguration(
            RouterModelId: intent.RouterModelId,
            ResourceGroupKey: intent.ResourceGroupKey,
            RuntimeProfileId: intent.RuntimeProfileId,
            LoadParams: loadParams,
            ParallelToolCalls: intent.ParallelToolCalls,
            RouterContextSize: intent.RouterContextSize,
            RouterCacheRamMib: intent.RouterCacheRamMib);

        return LocalRuntimeConfigurationParser.SerializeCanonical(config);
    }

    private static CatalogIntent? TryBuildIntent(StartModelDownloadRequest request)
    {
        var modelId = request.CatalogModelId?.Trim();
        var displayName = request.CatalogDisplayName?.Trim();
        var profileId = request.CatalogRuntimeProfileId?.Trim();
        var resourceGroupKey = request.CatalogResourceGroupKey?.Trim();

        if (string.IsNullOrWhiteSpace(modelId)
            || string.IsNullOrWhiteSpace(displayName)
            || string.IsNullOrWhiteSpace(profileId)
            || string.IsNullOrWhiteSpace(resourceGroupKey))
        {
            return null;
        }

        var description = string.IsNullOrWhiteSpace(request.CatalogDescription)
            ? null
            : request.CatalogDescription.Trim();

        return new CatalogIntent(
            ModelId: modelId,
            DisplayName: displayName,
            RuntimeProfileId: profileId,
            RouterModelId: request.RouterModelId.Trim(),
            Description: description,
            ResourceGroupKey: resourceGroupKey,
            IsActive: request.CatalogIsActive ?? true,
            DisplayOrder: request.CatalogDisplayOrder,
            LoadParamsJson: string.IsNullOrWhiteSpace(request.CatalogLoadParamsJson)
                ? null
                : request.CatalogLoadParamsJson.Trim(),
            ParallelToolCalls: request.CatalogParallelToolCalls ?? false,
            RouterContextSize: request.CatalogRouterContextSize,
            RouterCacheRamMib: request.CatalogRouterCacheRamMib);
    }

    private static CatalogIntent MapToCatalogIntent(LlamaCatalogDownloadIntent persistedIntent)
    {
        return new CatalogIntent(
            ModelId: persistedIntent.ModelId,
            DisplayName: persistedIntent.DisplayName,
            RuntimeProfileId: persistedIntent.RuntimeProfileId,
            RouterModelId: persistedIntent.RouterModelId,
            Description: persistedIntent.Description,
            ResourceGroupKey: persistedIntent.ResourceGroupKey,
            IsActive: persistedIntent.IsActive,
            DisplayOrder: persistedIntent.DisplayOrder,
            LoadParamsJson: persistedIntent.LoadParamsJson,
            ParallelToolCalls: persistedIntent.ParallelToolCalls,
            RouterContextSize: persistedIntent.RouterContextSize,
            RouterCacheRamMib: persistedIntent.RouterCacheRamMib);
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

    private sealed record CatalogIntent(
        string ModelId,
        string DisplayName,
        string RuntimeProfileId,
        string RouterModelId,
        string? Description,
        string ResourceGroupKey,
        bool IsActive,
        int? DisplayOrder,
        string? LoadParamsJson,
        bool ParallelToolCalls,
        int? RouterContextSize = null,
        int? RouterCacheRamMib = null);

    private sealed record CreateResult(
        bool Succeeded,
        AddModelErrorDto? Error)
    {
        public static CreateResult Success() => new(true, null);

        public static CreateResult Failure(AddModelErrorDto error) => new(false, error);
    }
}
