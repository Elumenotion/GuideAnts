using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
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

    public LocalModelOnboardingValidator(
        IConfiguration configuration,
        IApplicationSettingsService settingsService,
        IChatTargetValidator chatTargetValidator,
        ILlamaRuntimeInventoryService inventoryService,
        IHuggingFaceTokenResolver huggingFaceTokenResolver)
    {
        _configuration = configuration;
        _settingsService = settingsService;
        _chatTargetValidator = chatTargetValidator;
        _inventoryService = inventoryService;
        _huggingFaceTokenResolver = huggingFaceTokenResolver;
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

        ValidateRequiredFields(command);
        ValidateRouterKnobs(command);

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
            _chatTargetValidator.Validate(new ChatTarget(
                ModelId: command.CatalogModelId,
                Provider: "llama-cpp",
                RuntimeConfigJson: LocalModelOnboardingOrchestrator.BuildLlamaLocalRuntimeJson(command)));
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

        if (string.IsNullOrWhiteSpace(command.RuntimeProfileId))
        {
            throw new AddModelException(
                code: "RUNTIME_PROFILE_NOT_FOUND",
                step: "validation",
                message: "Runtime profile is required.",
                remediation: "Pick a runtime profile in Step 3.");
        }

        if (string.IsNullOrWhiteSpace(command.RouterModelId))
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: "Router alias is required.",
                remediation: "Pick a valid router alias in Step 3.");
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
