using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ILocalModelInstallationService
{
    Task<LlamaInstallationDetailDto> GetDetailAsync(string modelId, CancellationToken cancellationToken = default);
}

public sealed class LocalModelInstallationService : ILocalModelInstallationService
{
    private readonly ApplicationDbContext _db;

    public LocalModelInstallationService(
        ApplicationDbContext db,
        ILlamaRuntimeInventoryService inventoryService)
    {
        _db = db;
        // Inventory is live llama state; catalog edit must not wait on it.
        _ = inventoryService;
    }

    public async Task<LlamaInstallationDetailDto> GetDetailAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var normalized = modelId.Trim();
        var model = await _db.Models
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.ModelId == normalized, cancellationToken)
            .ConfigureAwait(false);

        if (model is null)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.InstallationNotFound,
                $"Model '{normalized}' was not found.",
                "Choose a valid catalog model id.",
                statusCode: 404);
        }

        var installation = await _db.LocalModelInstallations
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.ModelId == normalized, cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            throw new LocalModelLifecycleException(
                LocalModelLifecycleErrorCodes.InstallationNotFound,
                $"Model '{normalized}' has no installation provenance record.",
                "Install or attach the model before requesting installation detail.",
                statusCode: 404);
        }

        return MapDetailWithoutLiveRuntime(model, installation);
    }

    internal static SettingsModelDto MapModel(Model model) =>
        new(
            ModelId: model.ModelId,
            DisplayName: model.DisplayName,
            Provider: model.Provider,
            Description: model.Description,
            ReasoningChoicesJson: model.ReasoningChoicesJson,
            RuntimeConfigJson: model.RuntimeConfigJson,
            IsActive: model.IsActive,
            DisplayOrder: model.DisplayOrder,
            Created: model.Created,
            Updated: model.Updated,
            CombineSystemAndDeveloperMessages: model.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern: model.ThoughtBlockPattern,
            SamplingParametersJson: model.SamplingParametersJson,
            ThinkingControlJson: model.ThinkingControlJson,
            RequestFieldsWhenToolsPresentJson: model.RequestFieldsWhenToolsPresentJson);

    private static LlamaInstallationDetailDto MapDetailWithoutLiveRuntime(
        Model model,
        LocalModelInstallation installation)
    {
        // Catalog preset editing must not wait on llama-server /models. Live runtime
        // state belongs on the Loaded models tab; this DTO is SQL provenance + snapshot.
        return new LlamaInstallationDetailDto(
            ModelId: model.ModelId,
            CatalogModel: MapModel(model),
            CatalogId: installation.CatalogId,
            CatalogVersion: installation.CatalogVersion,
            Repository: installation.Repository,
            RequestedRevision: installation.RequestedRevision,
            ResolvedRevision: installation.ResolvedRevision,
            QuantId: installation.QuantId,
            QuantLabel: installation.QuantLabel,
            RouterModelId: installation.RouterModelId ?? string.Empty,
            TargetDirectory: installation.TargetDirectory ?? string.Empty,
            ModelArtifacts: InstallationArtifactRecords.Parse(installation.ModelArtifactsJson),
            ProjectorArtifacts: InstallationArtifactRecords.Parse(installation.ProjectorArtifactsJson),
            CompanionArtifacts: InstallationArtifactRecords.Parse(installation.CompanionArtifactsJson),
            RouterPresetSnapshot: InstallationArtifactRecords.ParsePresetSnapshot(installation.RouterPresetSnapshotJson),
            RuntimeState: "unknown",
            Loaded: false,
            CreatedUtc: installation.CreatedUtc,
            UpdatedUtc: installation.UpdatedUtc);
    }
}
