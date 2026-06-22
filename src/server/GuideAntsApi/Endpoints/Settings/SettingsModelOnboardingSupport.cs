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

    public static string? BuildCloudRuntimeConfigJson(AddModelRequest request)
    {
        if (request.ProviderConfig == null)
        {
            return null;
        }

        if (!request.ProviderConfig.TryGetPropertyValue("runtimeProfileId", out var profileIdNode)
            || profileIdNode is null)
        {
            return null;
        }

        string? profileId = null;
        if (profileIdNode is JsonValue strValue
            && strValue.TryGetValue<string>(out var parsedStr))
        {
            profileId = parsedStr?.Trim();
        }

        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        return JsonSerializer.Serialize(new { runtimeProfileId = profileId });
    }

    public static async Task<string?> DeriveCloudReasoningChoicesJsonAsync(
        IRuntimeProfileResolver runtimeProfileResolver,
        AddModelRequest request,
        CancellationToken cancellationToken)
    {
        var runtimeProfileId = GetProviderConfigString(request.ProviderConfig, "runtimeProfileId");
        if (string.IsNullOrWhiteSpace(runtimeProfileId))
        {
            return null;
        }

        var profile = await runtimeProfileResolver.ResolveAsync(runtimeProfileId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (profile.ThinkingControl?.ChoiceActions is null)
        {
            return null;
        }

        var choices = profile.ThinkingControl.ChoiceActions.Keys
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => choice.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return choices.Count == 0 ? null : JsonSerializer.Serialize(choices);
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
