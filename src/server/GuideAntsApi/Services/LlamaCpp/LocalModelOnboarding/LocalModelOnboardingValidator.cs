using System.Text.Json;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ILocalModelOnboardingValidator
{
    Task ValidateAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class LocalModelOnboardingValidator : ILocalModelOnboardingValidator
{
    private readonly IConfiguration _configuration;
    private readonly IApplicationSettingsService _settingsService;
    private readonly IChatTargetValidator _chatTargetValidator;
    private readonly ILlamaRuntimeInventoryService _inventoryService;
    private readonly IHuggingFaceTokenResolver _huggingFaceTokenResolver;
    private readonly ICuratedInstallResolver _curatedInstallResolver;
    private readonly ICustomInstallResolver _customInstallResolver;

    public LocalModelOnboardingValidator(
        IConfiguration configuration,
        IApplicationSettingsService settingsService,
        IChatTargetValidator chatTargetValidator,
        ILlamaRuntimeInventoryService inventoryService,
        IHuggingFaceTokenResolver huggingFaceTokenResolver,
        ICuratedInstallResolver curatedInstallResolver,
        ICustomInstallResolver customInstallResolver)
    {
        _configuration = configuration;
        _settingsService = settingsService;
        _chatTargetValidator = chatTargetValidator;
        _inventoryService = inventoryService;
        _huggingFaceTokenResolver = huggingFaceTokenResolver;
        _curatedInstallResolver = curatedInstallResolver;
        _customInstallResolver = customInstallResolver;
    }

    public async Task ValidateAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals((request.Provider ?? string.Empty).Trim(), "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Local model onboarding is only valid for the llama-cpp provider.",
                remediation: "Choose the llama-cpp provider and retry.");
        }

        if (string.Equals(command.InstallSource, LocalModelInstallSources.Curated, StringComparison.OrdinalIgnoreCase))
        {
            await ValidateCuratedAsync(request, command, cancellationToken).ConfigureAwait(false);
            return;
        }

        var usesRowOwnedChatBehavior = command.ExplicitHuggingFace is not null
            || string.Equals(command.InstallSource, LocalModelInstallSources.ExistingAlias, StringComparison.OrdinalIgnoreCase);
        if (usesRowOwnedChatBehavior)
        {
            ValidateRowOwnedChatBehavior(command);
        }

        if (command.ExplicitHuggingFace is not null)
        {
            await ValidateCustomExplicitAsync(request, command, cancellationToken).ConfigureAwait(false);
            return;
        }

        ValidateRequiredFields(command);
        ValidateRouterKnobs(command);

        if (string.Equals(command.InstallSource, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase))
        {
            ValidateRowOwnedChatBehavior(command);
        }

        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            throw new AddModelException(
                code: "PROVIDER_CREDENTIALS_MISSING",
                step: "validation",
                message: "Provider section 'LlamaCpp' is not ready: no local llama server is configured for this container yet.",
                remediation: "Configure a llama server base URL for this container, or choose a cloud chat provider instead.");
        }

        var existingModels = await _settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        if (existingModels.Any(model => string.Equals(model.ModelId, command.CatalogModelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AddModelException(
                code: "MODEL_ID_TAKEN",
                step: "validation",
                message: $"Model '{command.CatalogModelId}' already exists.",
                remediation: "Back up to Step 2 and choose a different model ID.");
        }

        try
        {
            ValidateLlamaInstallTarget(command);
        }
        catch (RoutingException ex)
        {
            throw MapRoutingException(ex);
        }

        var inventory = await _inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(command.InstallSource, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_huggingFaceTokenResolver.Resolve()))
            {
                throw new AddModelException(
                    code: "HUGGINGFACE_TOKEN_MISSING",
                    step: "validation",
                    message: "No Hugging Face token is configured.",
                    remediation: "Open Connections → Hugging Face and save a token before retrying.");
            }

            var existingAlias = inventory.FirstOrDefault(item =>
                string.Equals(item.RouterModelId, command.RouterModelId, StringComparison.Ordinal));
            if (existingAlias is not null && existingAlias.CatalogModelIds.Count > 0)
            {
                throw new AddModelException(
                    code: "ROUTER_ALIAS_TAKEN",
                    step: "validation",
                    message: $"Router alias '{existingAlias.RouterModelId}' is already referenced by catalog rows: {string.Join(", ", existingAlias.CatalogModelIds)}.",
                    remediation: "Back up to Step 3 and choose a different alias.");
            }

            return;
        }

        var row = inventory.FirstOrDefault(item => string.Equals(item.RouterModelId, command.RouterModelId, StringComparison.Ordinal));
        if (row is null)
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: $"Router alias '{command.RouterModelId}' does not exist.",
                remediation: "Back up and pick a live alias from Runtime Inventory.");
        }

        if (!row.HasModelFile)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: $"Router alias '{command.RouterModelId}' is missing a model artifact on disk.",
                remediation: "Repair the alias in Local Llama Runtime or re-install it from Hugging Face.");
        }

        if (row.CatalogModelIds.Count > 0)
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: $"Router alias '{command.RouterModelId}' is already adopted by catalog rows: {string.Join(", ", row.CatalogModelIds)}.",
                remediation: "Pick an orphaned alias or delete the existing catalog row first.");
        }
    }

    private async Task ValidateCuratedAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        ValidateCuratedForbiddenFields(request);

        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            throw new AddModelException(
                code: "PROVIDER_CREDENTIALS_MISSING",
                step: "validation",
                message: "Provider section 'LlamaCpp' is not ready: no local llama server is configured for this container yet.",
                remediation: "Configure a llama server base URL for this container, or choose a cloud chat provider instead.");
        }

        var immutableInput = await _curatedInstallResolver
            .ResolveAsync(request, command, cancellationToken)
            .ConfigureAwait(false);

        // Re-installing the same curated definition is a reconcile, not a conflict. When a model row
        // with this catalogModelId already exists AND it targets the same router alias + runtime
        // profile, allow the install to proceed: the operation is idempotent (already-downloaded
        // artifacts are skipped) and will fetch any artifacts the current definition now requires
        // (e.g. a projector added to the catalog after the original text-only install).
        var existingModels = await _settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        var existingModel = existingModels.FirstOrDefault(model =>
            string.Equals(model.ModelId, immutableInput.CatalogModelId, StringComparison.Ordinal));
        var isReconcile = existingModel is not null && IsSameCuratedTarget(existingModel, immutableInput);
        if (existingModel is not null && !isReconcile)
        {
            throw new AddModelException(
                code: "MODEL_ID_TAKEN",
                step: "validation",
                message: $"Model '{immutableInput.CatalogModelId}' already exists and targets a different runtime configuration.",
                remediation: "Choose a different catalog definition or remove the existing model.");
        }

        try
        {
            _ = LocalRuntimeConfigurationParser.ParseRequired(
                immutableInput.CatalogModelId,
                LocalRuntimeConfigurationParser.SerializeCanonical(
                    new LocalRuntimeConfiguration(immutableInput.RouterModelId)));
        }
        catch (InvalidOperationException ex)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: ex.Message,
                remediation: "Fix the curated install configuration and retry.");
        }

        var inventory = await _inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var existingAlias = inventory.FirstOrDefault(item =>
            string.Equals(item.RouterModelId, immutableInput.RouterModelId, StringComparison.Ordinal));
        var conflictingCatalogRows = existingAlias?.CatalogModelIds
            .Where(id => !string.Equals(id, immutableInput.CatalogModelId, StringComparison.Ordinal))
            .ToList() ?? [];
        if (conflictingCatalogRows.Count > 0)
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: $"Router alias '{existingAlias!.RouterModelId}' is already referenced by catalog rows: {string.Join(", ", conflictingCatalogRows)}.",
                remediation: "Remove the existing catalog row or choose another curated definition.");
        }
    }

    private static bool IsSameCuratedTarget(SettingsModelDto existing, CuratedImmutableOperationInput immutableInput)
    {
        if (!string.Equals(existing.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(existing.RuntimeConfigJson))
        {
            return false;
        }

        try
        {
            var parsed = LocalRuntimeConfigurationParser.Parse(existing.ModelId, existing.RuntimeConfigJson);
            return string.Equals(parsed.RouterModelId, immutableInput.RouterModelId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateCuratedForbiddenFields(AddModelRequest request)
    {
        var install = request.Install!;
        var forbidden = new List<string>();

        if (install.HuggingFace is not null)
        {
            forbidden.Add("install.huggingFace");
        }

        if (install.ExistingAlias is not null)
        {
            forbidden.Add("install.existingAlias");
        }

        if (!string.IsNullOrWhiteSpace(install.RouterModelId))
        {
            forbidden.Add("install.routerModelId");
        }

        if (install.RouterContextSize is not null)
        {
            forbidden.Add("install.routerContextSize");
        }

        if (install.RouterCacheRamMib is not null)
        {
            forbidden.Add("install.routerCacheRamMib");
        }

        if (forbidden.Count > 0)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CuratedForbiddenField,
                step: "validation",
                message: $"Curated install accepts identities only. Remove forbidden field(s): {string.Join(", ", forbidden)}.",
                remediation: "Submit only source, catalogId, catalogVersion, quantId, and resolvedRevision under install.");
        }

        var curated = install.Curated;
        if (curated is null
            || string.IsNullOrWhiteSpace(curated.CatalogId)
            || string.IsNullOrWhiteSpace(curated.CatalogVersion)
            || string.IsNullOrWhiteSpace(curated.QuantId)
            || string.IsNullOrWhiteSpace(curated.ResolvedRevision))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Curated install requires catalogId, catalogVersion, quantId, and resolvedRevision.",
                remediation: "Complete curated quant selection and retry.");
        }
    }

    private static void ValidateRequiredFields(LocalModelOnboardingCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.CatalogModelId))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Model ID is required.",
                remediation: "Enter a unique catalog model ID in Step 2.");
        }

        if (string.IsNullOrWhiteSpace(command.CatalogDisplayName))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Display name is required.",
                remediation: "Enter a display name in Step 2.");
        }

        if (string.IsNullOrWhiteSpace(command.RouterModelId))
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: "Router alias is required.",
                remediation: "Pick a valid router alias in Step 3.");
        }

        if (command.ExplicitHuggingFace is null
            && string.Equals(command.InstallSource, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase)
            && !command.HasProviderConfigChatBehavior)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Model chat behavior JSON is required for Hugging Face onboarding.",
                remediation: "Provide samplingParametersJson and thinkingControlJson in providerConfig.");
        }

        if (string.Equals(command.InstallSource, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(command.Repository)
                || string.IsNullOrWhiteSpace(command.QuantIncludePattern)
                || string.IsNullOrWhiteSpace(command.TargetDirectory))
            {
                throw new AddModelException(
                    code: "INSTALL_STEP_FAILED",
                    step: "validation",
                    message: "Repository, quant pattern, and target directory are required. mmproj pattern is optional.",
                    remediation: "Complete the Hugging Face source fields in Step 3.");
            }
        }
    }

    private static void ValidateRouterKnobs(LocalModelOnboardingCommand command)
    {
        if (command.RouterContextSize is { } contextSize
            && (contextSize < 1024 || contextSize > 1_048_576))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Context size must be a whole number from 1024 to 1048576.",
                remediation: "Enter a valid context size or leave it blank to use container defaults.");
        }

        if (command.RouterCacheRamMib is { } cacheRamMib
            && (cacheRamMib < 0 || cacheRamMib > 262_144))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Prompt cache RAM (MiB) must be a whole number from 0 to 262144.",
                remediation: "Enter a valid cache value or leave it blank to use container defaults.");
        }
    }

    private static void ValidateRowOwnedChatBehavior(LocalModelOnboardingCommand command)
    {
        if (!command.HasProviderConfigChatBehavior)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Model chat behavior is required for custom and existing-alias onboarding.",
                remediation: "Submit providerConfig.thinkingControlJson and the model chat behavior fields.");
        }

        try
        {
            using var document = JsonDocument.Parse(command.ThinkingControlJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("thinkingControlJson must be a JSON object.");
            }

            _ = command.ToRowOwnedRuntimeProfileData();
        }
        catch (JsonException ex)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: $"Model chat behavior is invalid: {ex.Message}",
                remediation: "Submit a valid providerConfig.thinkingControlJson object.",
                innerException: ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: $"Model chat behavior is invalid: {ex.Message}",
                remediation: "Correct the providerConfig chat behavior JSON fields.",
                innerException: ex);
        }
    }

    private async Task ValidateCustomExplicitAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            throw new AddModelException(
                code: "PROVIDER_CREDENTIALS_MISSING",
                step: "validation",
                message: "Provider section 'LlamaCpp' is not ready: no local llama server is configured for this container yet.",
                remediation: "Configure a llama server base URL for this container, or choose a cloud chat provider instead.");
        }

        await _customInstallResolver.ResolveAsync(request, command, cancellationToken).ConfigureAwait(false);

        var existingModels = await _settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        if (existingModels.Any(model => string.Equals(model.ModelId, command.CatalogModelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AddModelException(
                code: "MODEL_ID_TAKEN",
                step: "validation",
                message: $"Model '{command.CatalogModelId}' already exists.",
                remediation: "Back up to Step 2 and choose a different model ID.");
        }

        try
        {
            ValidateLlamaInstallTarget(command);
        }
        catch (RoutingException ex)
        {
            throw MapRoutingException(ex);
        }

        var inventory = await _inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var existingAlias = inventory.FirstOrDefault(item =>
            string.Equals(item.RouterModelId, command.RouterModelId, StringComparison.Ordinal));
        if (existingAlias is not null && existingAlias.CatalogModelIds.Count > 0)
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: $"Router alias '{existingAlias.RouterModelId}' is already referenced by catalog rows: {string.Join(", ", existingAlias.CatalogModelIds)}.",
                remediation: "Back up to Step 3 and choose a different alias.");
        }
    }

    private void ValidateLlamaInstallTarget(
        LocalModelOnboardingCommand command)
    {
        var chatBehavior = command.ToRowOwnedRuntimeProfileData();

        _chatTargetValidator.Validate(new ChatTarget(
            ModelId: command.CatalogModelId,
            Provider: "llama-cpp",
            RuntimeConfigJson: LocalModelOnboardingOrchestrator.BuildLlamaLocalRuntimeJson(command),
            ChatBehavior: chatBehavior));
    }

    private static AddModelException MapRoutingException(RoutingException exception)
    {
        var code = exception.Code switch
        {
            RoutingErrorCodes.ProviderNotReady => "PROVIDER_CREDENTIALS_MISSING",
            RoutingErrorCodes.RuntimeNotReady => "RUNTIME_PROFILE_NOT_FOUND",
            _ => "INSTALL_STEP_FAILED",
        };

        return new AddModelException(
            code: code,
            step: "validation",
            message: exception.Message,
            remediation: exception.Action,
            innerException: exception);
    }
}
