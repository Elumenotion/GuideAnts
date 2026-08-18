using System.Text.Json;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Maps catalog <see cref="GuideAntsApi.DataModel.Models.Model"/> chat-behavior columns
/// to profile-shaped data for routing and chat clients. Model rows are authority at inference
/// for every provider. See docs/model-chat-behavior-contract.md.
/// </summary>
public static class ModelChatBehavior
{
    public static RuntimeProfileData ToRuntimeProfileData(Model model)
    {
        return RuntimeProfileDataJson.FromJsonStrings(
            model.ModelId,
            model.CombineSystemAndDeveloperMessages,
            model.ThoughtBlockPattern,
            model.SamplingParametersJson,
            model.ThinkingControlJson,
            model.RequestFieldsWhenToolsPresentJson,
            model.DisplayName,
            model.Description);
    }

    public static RuntimeProfileData ToRowOwnedSamplingProfile(Model model)
    {
        return RuntimeProfileDataJson.FromJsonStrings(
            model.ModelId,
            model.CombineSystemAndDeveloperMessages,
            model.ThoughtBlockPattern,
            model.SamplingParametersJson,
            "{}",
            "{}",
            model.DisplayName,
            model.Description);
    }

    public static bool HasRowOwnedSamplingSurface(Model model)
    {
        return !string.IsNullOrWhiteSpace(model.SamplingParametersJson)
            && !string.Equals(model.SamplingParametersJson.Trim(), "{}", StringComparison.Ordinal);
    }

    public static bool HasConfiguredBehavior(Model model)
    {
        return !string.IsNullOrWhiteSpace(model.ThinkingControlJson)
            && !string.Equals(model.ThinkingControlJson.Trim(), "{}", StringComparison.Ordinal);
    }

    public static void ApplyFromRuntimeProfileData(Model model, RuntimeProfileData profile)
    {
        model.CombineSystemAndDeveloperMessages = profile.CombineSystemAndDeveloperMessages;
        model.ThoughtBlockPattern = profile.ThoughtBlockPattern;
        model.SamplingParametersJson = JsonSerializer.Serialize(profile.SamplingParameters);
        model.ThinkingControlJson = JsonSerializer.Serialize(profile.ThinkingControl);
        model.RequestFieldsWhenToolsPresentJson = JsonSerializer.Serialize(profile.RequestFieldsWhenToolsPresent);
    }

    public static CreateSettingsModelRequest BuildCreateRequest(
        AddModelCatalogDto catalog,
        string provider,
        string? reasoningChoicesJson,
        string? runtimeConfigJson,
        RuntimeProfileData profile)
    {
        return new CreateSettingsModelRequest(
            ModelId: catalog.ModelId.Trim(),
            DisplayName: catalog.DisplayName.Trim(),
            Provider: provider.Trim(),
            Description: string.IsNullOrWhiteSpace(catalog.Description) ? null : catalog.Description.Trim(),
            ReasoningChoicesJson: reasoningChoicesJson,
            RuntimeConfigJson: runtimeConfigJson,
            CombineSystemAndDeveloperMessages: profile.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern: profile.ThoughtBlockPattern,
            SamplingParametersJson: JsonSerializer.Serialize(profile.SamplingParameters),
            ThinkingControlJson: JsonSerializer.Serialize(profile.ThinkingControl),
            RequestFieldsWhenToolsPresentJson: JsonSerializer.Serialize(profile.RequestFieldsWhenToolsPresent),
            IsActive: catalog.IsActive,
            DisplayOrder: catalog.DisplayOrder);
    }

    public static CreateSettingsModelRequest BuildCreateRequestWithDefaults(
        AddModelCatalogDto catalog,
        string provider,
        string? reasoningChoicesJson,
        string? runtimeConfigJson)
    {
        return new CreateSettingsModelRequest(
            ModelId: catalog.ModelId.Trim(),
            DisplayName: catalog.DisplayName.Trim(),
            Provider: provider.Trim(),
            Description: string.IsNullOrWhiteSpace(catalog.Description) ? null : catalog.Description.Trim(),
            ReasoningChoicesJson: reasoningChoicesJson,
            RuntimeConfigJson: runtimeConfigJson,
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingParametersJson: "{}",
            ThinkingControlJson: "{}",
            RequestFieldsWhenToolsPresentJson: "{}",
            IsActive: catalog.IsActive,
            DisplayOrder: catalog.DisplayOrder);
    }

    public static string? DeriveReasoningChoicesJson(string thinkingControlJson)
    {
        if (string.IsNullOrWhiteSpace(thinkingControlJson))
        {
            return null;
        }

        try
        {
            var thinkingControl = RuntimeProfileDataJson.DeserializeThinkingControl(thinkingControlJson, "(model)");
            var choices = thinkingControl.ChoiceActions.Keys
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .Select(choice => choice.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return choices.Count == 0 ? null : JsonSerializer.Serialize(choices);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
