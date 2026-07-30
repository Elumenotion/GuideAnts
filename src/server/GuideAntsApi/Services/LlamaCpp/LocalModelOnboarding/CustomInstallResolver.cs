using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ICustomInstallResolver
{
    Task<CustomInstallImmutableInput> ResolveAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CustomInstallResolver : ICustomInstallResolver
{
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly ILlamaRuntimeAdminClient _adminClient;

    public CustomInstallResolver(
        IHuggingFaceTokenResolver tokenResolver,
        ILlamaRuntimeAdminClient adminClient)
    {
        _tokenResolver = tokenResolver;
        _adminClient = adminClient;
    }

    public Task<CustomInstallImmutableInput> ResolveAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        var explicitInput = command.ExplicitHuggingFace
            ?? throw new AddModelException(
                "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Explicit Hugging Face artifact input is required.",
                remediation: "Submit repository, resolvedRevision, modelFiles, targetDirectory, and routerPreset.");

        if (string.IsNullOrWhiteSpace(explicitInput.Repository)
            || string.IsNullOrWhiteSpace(explicitInput.ResolvedRevision)
            || explicitInput.ModelFiles.Count == 0
            || string.IsNullOrWhiteSpace(explicitInput.TargetDirectory))
        {
            throw new AddModelException(
                "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Repository, resolvedRevision, modelFiles, and targetDirectory are required.",
                remediation: "Complete all custom Hugging Face fields.");
        }

        if (string.IsNullOrWhiteSpace(_tokenResolver.Resolve()))
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.HuggingFaceTokenMissing,
                step: "validation",
                message: "No Hugging Face token is configured.",
                remediation: "Open Connections → Hugging Face and save a token before retrying.");
        }

        var routerPreset = RouterPresetValidator.ValidateAndNormalize(explicitInput.RouterPreset);

        var mmprojFiles = explicitInput.MmprojFiles?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            ?? new List<string>();

        return Task.FromResult(new CustomInstallImmutableInput(
            CatalogModelId: command.CatalogModelId,
            CatalogDisplayName: command.CatalogDisplayName,
            CatalogDescription: command.CatalogDescription,
            CatalogDisplayOrder: command.CatalogDisplayOrder,
            CatalogIsActive: command.CatalogIsActive,
            Repository: explicitInput.Repository.Trim(),
            RequestedRevision: explicitInput.ResolvedRevision.Trim(),
            ResolvedRevision: explicitInput.ResolvedRevision.Trim(),
            ModelFiles: explicitInput.ModelFiles.Select(p => p.Trim()).ToList(),
            MmprojFiles: mmprojFiles.Select(p => p.Trim()).ToList(),
            RouterModelId: command.RouterModelId,
            SamplingParametersJson: command.SamplingParametersJson,
            ReasoningChoicesJson: command.ReasoningChoicesJson,
            ThinkingControlJson: command.ThinkingControlJson,
            RequestFieldsWhenToolsPresentJson: command.RequestFieldsWhenToolsPresentJson,
            CombineSystemAndDeveloperMessages: command.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern: command.ThoughtBlockPattern,
            TargetDirectory: explicitInput.TargetDirectory.Trim(),
            RouterPreset: routerPreset));
    }
}
