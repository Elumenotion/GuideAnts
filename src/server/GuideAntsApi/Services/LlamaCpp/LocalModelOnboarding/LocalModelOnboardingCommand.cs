using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public static class LocalModelInstallSources
{
    public const string HuggingFace = "huggingface";
    public const string ExistingAlias = "existingAlias";
}

public sealed record LocalModelOnboardingCommand(
    string CatalogModelId,
    string CatalogDisplayName,
    string? CatalogDescription,
    int? CatalogDisplayOrder,
    bool CatalogIsActive,
    string RuntimeProfileId,
    string RouterModelId,
    string InstallSource,
    string? Repository,
    string? QuantIncludePattern,
    string? MmprojIncludePattern,
    string? TargetDirectory,
    int? RouterContextSize,
    int? RouterCacheRamMib,
    string? OnboardingUi)
{
    public static LocalModelOnboardingCommand FromAddModelRequest(AddModelRequest request)
    {
        var install = request.Install
            ?? throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Install details are required for llama-cpp models.",
                remediation: "Complete Step 3 and pick an install source.");

        var source = (install.Source ?? string.Empty).Trim();
        if (!string.Equals(source, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, LocalModelInstallSources.ExistingAlias, StringComparison.OrdinalIgnoreCase))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Install source must be either 'huggingface' or 'existingAlias'.",
                remediation: "Choose a valid install source and retry.");
        }

        var routerModelId = (install.RouterModelId ?? string.Empty).Trim();
        var runtimeProfileId = (install.RuntimeProfileId ?? string.Empty).Trim();

        string? repository = null;
        string? quantIncludePattern = null;
        string? mmprojIncludePattern = null;
        string? targetDirectory = null;

        if (string.Equals(source, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase))
        {
            repository = (install.HuggingFace?.Repository ?? string.Empty).Trim();
            quantIncludePattern = (install.HuggingFace?.QuantIncludePattern ?? string.Empty).Trim();
            mmprojIncludePattern = (install.HuggingFace?.MmprojIncludePattern ?? string.Empty).Trim();
            targetDirectory = (install.HuggingFace?.TargetDirectory ?? string.Empty).Trim();
        }

        return new LocalModelOnboardingCommand(
            CatalogModelId: (request.Catalog.ModelId ?? string.Empty).Trim(),
            CatalogDisplayName: (request.Catalog.DisplayName ?? string.Empty).Trim(),
            CatalogDescription: string.IsNullOrWhiteSpace(request.Catalog.Description)
                ? null
                : request.Catalog.Description.Trim(),
            CatalogDisplayOrder: request.Catalog.DisplayOrder,
            CatalogIsActive: request.Catalog.IsActive,
            RuntimeProfileId: runtimeProfileId,
            RouterModelId: routerModelId,
            InstallSource: source,
            Repository: repository,
            QuantIncludePattern: quantIncludePattern,
            MmprojIncludePattern: mmprojIncludePattern,
            TargetDirectory: targetDirectory,
            RouterContextSize: install.RouterContextSize,
            RouterCacheRamMib: install.RouterCacheRamMib,
            OnboardingUi: request.ProviderConfig is null
                ? null
                : GetProviderConfigString(request.ProviderConfig, "onboardingUi")?.Trim());
    }

    private static string? GetProviderConfigString(System.Text.Json.Nodes.JsonObject providerConfig, string propertyName)
    {
        if (!providerConfig.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        if (node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var str))
        {
            return str;
        }

        return null;
    }
}
