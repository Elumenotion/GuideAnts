using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

internal static class SettingsModelOnboardingSupport
{
    public static async Task ValidateAddModelRequestAsync(
        AddModelRequest request,
        IApplicationSettingsService settingsService,
        IChatTargetValidator chatTargetValidator,
        CancellationToken cancellationToken)
    {
        if (request.Catalog is null)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Catalog details are required.",
                remediation: "Fill out the catalog step and try again.");
        }

        var provider = (request.Provider ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(provider) || !ChatTargetValidator.KnownProviders.Contains(provider))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: $"Provider '{request.Provider}' is not supported.",
                remediation: "Pick one of the supported providers and try again.");
        }

        var modelId = (request.Catalog.ModelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Model ID is required.",
                remediation: "Enter a unique catalog model ID in Step 2.");
        }

        var displayName = (request.Catalog.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Display name is required.",
                remediation: "Enter a display name in Step 2.");
        }

        var existingModels = await settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        if (existingModels.Any(model => string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AddModelException(
                code: "MODEL_ID_TAKEN",
                step: "validation",
                message: $"Model '{modelId}' already exists.",
                remediation: "Back up to Step 2 and choose a different model ID.");
        }

        try
        {
            chatTargetValidator.Validate(new ChatTarget(
                ModelId: modelId,
                Provider: provider,
                RuntimeConfigJson: null));
        }
        catch (RoutingException ex)
        {
            throw MapAddModelRoutingException(ex);
        }
    }

    public static CreateSettingsModelRequest BuildModelCreateRequest(
        AddModelRequest request,
        string? reasoningChoicesJson,
        string? runtimeConfigJson,
        RuntimeProfileData? behaviorTemplate = null)
    {
        if (behaviorTemplate is not null)
        {
            return ModelChatBehavior.BuildCreateRequest(
                request.Catalog,
                request.Provider.Trim(),
                reasoningChoicesJson,
                runtimeConfigJson,
                behaviorTemplate);
        }

        return ModelChatBehavior.BuildCreateRequestWithDefaults(
            request.Catalog,
            request.Provider.Trim(),
            reasoningChoicesJson,
            runtimeConfigJson);
    }

    public static CreateSettingsModelRequest BuildCloudModelCreateRequest(AddModelRequest request)
    {
        var samplingParametersJson = GetProviderConfigString(request.ProviderConfig, "samplingParametersJson");
        var reasoningChoicesJson = GetProviderConfigString(request.ProviderConfig, "reasoningChoicesJson");

        return new CreateSettingsModelRequest(
            ModelId: request.Catalog.ModelId.Trim(),
            DisplayName: request.Catalog.DisplayName.Trim(),
            Provider: request.Provider.Trim(),
            Description: string.IsNullOrWhiteSpace(request.Catalog.Description) ? null : request.Catalog.Description.Trim(),
            ReasoningChoicesJson: string.IsNullOrWhiteSpace(reasoningChoicesJson) ? null : reasoningChoicesJson.Trim(),
            RuntimeConfigJson: null,
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingParametersJson: string.IsNullOrWhiteSpace(samplingParametersJson) ? "{}" : samplingParametersJson.Trim(),
            ThinkingControlJson: "{}",
            RequestFieldsWhenToolsPresentJson: "{}",
            IsActive: request.Catalog.IsActive,
            DisplayOrder: request.Catalog.DisplayOrder);
    }

    public static string? BuildCloudRuntimeConfigJson(AddModelRequest request)
    {
        _ = request;
        return null;
    }

    public static string NormalizeRouteModelId(string modelId)
    {
        var raw = (modelId ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return raw;
        }

        try
        {
            return Uri.UnescapeDataString(raw).Trim();
        }
        catch (FormatException)
        {
            return raw;
        }
    }

    public static AddModelErrorDto MapAddModelRoutingError(RoutingException exception)
        => MapAddModelRoutingException(exception).ToDto();

    private static string? GetProviderConfigString(JsonObject? providerConfig, string propertyName)
    {
        if (providerConfig is null
            || !providerConfig.TryGetPropertyValue(propertyName, out var node)
            || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var str))
        {
            return str;
        }

        return null;
    }

    private static AddModelException MapAddModelRoutingException(RoutingException exception)
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
