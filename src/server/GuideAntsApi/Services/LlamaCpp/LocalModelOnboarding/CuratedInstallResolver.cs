using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public interface ICuratedInstallResolver
{
    Task<CuratedImmutableOperationInput> ResolveAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CuratedInstallResolver : ICuratedInstallResolver
{
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;

    public CuratedInstallResolver(
        ILlamaRuntimeAdminClient adminClient,
        IHuggingFaceTokenResolver tokenResolver)
    {
        _adminClient = adminClient;
        _tokenResolver = tokenResolver;
    }

    public async Task<CuratedImmutableOperationInput> ResolveAsync(
        AddModelRequest request,
        LocalModelOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        var curated = command.Curated
            ?? throw new AddModelException(
                CuratedInstallErrorCodes.CatalogDefinitionNotFound,
                step: "validation",
                message: "Curated install identities are required.",
                remediation: "Submit catalogId, catalogVersion, quantId, and resolvedRevision.");

        var catalogId = curated.CatalogId.Trim();
        var catalogVersion = curated.CatalogVersion.Trim();
        var quantId = curated.QuantId.Trim();
        var clientRevision = curated.ResolvedRevision.Trim();

        LlamaCatalogResponseDto catalog;
        try
        {
            catalog = await _adminClient.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new AddModelException(
                "LLAMA_CATALOG_UNAVAILABLE",
                step: "validation",
                message: ex.Message,
                remediation: "Ensure llama-admin is reachable and retry.");
        }

        if (!string.Equals(catalog.CatalogVersion, catalogVersion, StringComparison.Ordinal))
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CatalogVersionUnavailable,
                step: "validation",
                message: $"Catalog version '{catalogVersion}' is not available. Shipped version is '{catalog.CatalogVersion}'.",
                remediation: "Refresh the catalog and choose a definition from the current version.");
        }

        var definition = catalog.Models.FirstOrDefault(m => string.Equals(m.Id, catalogId, StringComparison.Ordinal));
        if (definition is null)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CatalogDefinitionNotFound,
                step: "validation",
                message: $"Catalog definition '{catalogId}' was not found.",
                remediation: "Refresh the catalog and choose a valid definition.");
        }

        var hfToken = _tokenResolver.Resolve();
        if (string.IsNullOrWhiteSpace(hfToken))
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.HuggingFaceTokenMissing,
                step: "validation",
                message: "No Hugging Face token is configured.",
                remediation: "Open Connections → Hugging Face and save a token before retrying.");
        }

        LlamaCatalogQuantsResponseDto quantsAtHead;
        try
        {
            quantsAtHead = await _adminClient
                .GetCatalogQuantsAsync(catalogId, catalogVersion, hfToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlamaCatalogServiceException ex) when (ex.Code is "HUGGINGFACE_TOKEN_MISSING" or "REPO_TOKEN_INSUFFICIENT")
        {
            throw new AddModelException(
                ex.Code,
                step: "validation",
                message: ex.Message,
                remediation: "Configure a Hugging Face token with access to this repository.");
        }
        catch (LlamaCatalogServiceException ex) when (ex.Code == "CATALOG_VERSION_MISMATCH")
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CatalogVersionUnavailable,
                step: "validation",
                message: ex.Message,
                remediation: "Refresh the catalog and choose a definition from the current version.");
        }
        catch (LlamaCatalogServiceException ex)
        {
            throw new AddModelException(
                ex.Code,
                step: "validation",
                message: ex.Message,
                remediation: "Refresh repository metadata and retry.");
        }

        if (!string.Equals(quantsAtHead.ResolvedRevision, clientRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CommitChanged,
                step: "validation",
                message: $"Repository commit changed. Requested '{clientRevision}' but current resolved revision is '{quantsAtHead.ResolvedRevision}'.",
                remediation: "Refresh quant selection and submit the current resolved revision.");
        }

        LlamaCatalogQuantsResponseDto quantsAtCommit;
        try
        {
            quantsAtCommit = await _adminClient
                .GetCatalogQuantsAsync(
                    catalogId,
                    catalogVersion,
                    hfToken,
                    cancellationToken,
                    resolvedRevision: clientRevision)
                .ConfigureAwait(false);
        }
        catch (LlamaCatalogServiceException ex) when (ex.Code is "REPOSITORY_NOT_FOUND" or "COMMIT_UNAVAILABLE")
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CommitUnavailable,
                step: "validation",
                message: ex.Message,
                remediation: "Refresh quant selection or choose another revision.");
        }
        catch (LlamaCatalogServiceException ex)
        {
            throw new AddModelException(
                ex.Code,
                step: "validation",
                message: ex.Message,
                remediation: "Refresh repository metadata and retry.");
        }

        if (!string.Equals(quantsAtCommit.ResolvedRevision, clientRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.CommitUnavailable,
                step: "validation",
                message: $"Resolved revision '{clientRevision}' is no longer available.",
                remediation: "Refresh quant selection and retry.");
        }

        var quant = quantsAtCommit.Quants.FirstOrDefault(q => string.Equals(q.Id, quantId, StringComparison.Ordinal));
        if (quant is null)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.QuantMissing,
                step: "validation",
                message: $"Quant '{quantId}' is not available at commit '{clientRevision}'.",
                remediation: "Refresh quant selection and choose an available group.");
        }

        if (quant.Files.Count == 0)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.QuantIncomplete,
                step: "validation",
                message: $"Quant '{quantId}' has no artifacts at commit '{clientRevision}'.",
                remediation: "Choose another quant group.");
        }

        var shardCounts = quant.Files
            .Where(f => f.ShardCount is > 0)
            .Select(f => f.ShardCount!.Value)
            .Distinct()
            .ToList();
        if (shardCounts.Count > 1)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.QuantIncomplete,
                step: "validation",
                message: $"Quant '{quantId}' has mixed shard totals.",
                remediation: "Choose another quant group.");
        }

        if (shardCounts.Count == 1)
        {
            var expected = shardCounts[0];
            var shardIndexes = quant.Files
                .Where(f => f.ShardIndex is > 0)
                .Select(f => f.ShardIndex!.Value)
                .OrderBy(i => i)
                .ToList();
            if (shardIndexes.Count != expected
                || shardIndexes.Distinct().Count() != expected
                || shardIndexes.First() != 1
                || shardIndexes.Last() != expected)
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.QuantIncomplete,
                    step: "validation",
                    message: $"Quant '{quantId}' is missing one or more shards at commit '{clientRevision}'.",
                    remediation: "Refresh quant selection or choose another group.");
            }
        }

        var defaults = definition.Defaults;
        var requiresProjector = defaults.Mmproj is not null;
        if (requiresProjector && quantsAtCommit.Projector is null)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.ProjectorMissing,
                step: "validation",
                message: $"Projector artifacts are required for '{catalogId}' but none were resolved at commit '{clientRevision}'.",
                remediation: "Refresh repository metadata or choose another definition.");
        }

        if (!requiresProjector && quantsAtCommit.Projector is not null)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.PresetInvalid,
                step: "validation",
                message: $"Definition '{catalogId}' does not allow a projector but one was resolved.",
                remediation: "Refresh the catalog definition.");
        }

        try
        {
            ManifestChatBehavior.Validate(defaults.ChatBehavior, catalogId);
        }
        catch (InvalidOperationException ex)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.PresetInvalid,
                step: "validation",
                message: ex.Message,
                remediation: "Fix chatBehavior in the catalog manifest for this definition.");
        }

        var routerPreset = RouterPresetValidator.ValidateAndNormalize(defaults.RouterPreset);
        var chatBehavior = defaults.ChatBehavior;
        var samplingParametersJson = ManifestChatBehavior.SerializeSamplingParameters(chatBehavior);
        var thinkingControlJson = ManifestChatBehavior.SerializeThinkingControl(chatBehavior);
        var requestFieldsWhenToolsPresentJson =
            ManifestChatBehavior.SerializeRequestFieldsWhenToolsPresent(chatBehavior);
        var reasoningChoicesJson = ManifestChatBehavior.DeriveReasoningChoicesJson(chatBehavior);

        var modelFiles = quant.Files.Select(f => f.Path).ToList();
        var mmprojFiles = quantsAtCommit.Projector is null
            ? Array.Empty<string>()
            : new[] { quantsAtCommit.Projector.Path };
        var companions = quantsAtCommit.Companions ?? Array.Empty<LlamaProjectorArtifactDto>();
        var companionFiles = companions.Select(c => c.Path).ToList();

        var artifactMetadata = BuildArtifactMetadata(quant, quantsAtCommit.Projector, companions);

        var catalogModelId = string.IsNullOrWhiteSpace(request.Catalog.ModelId)
            ? defaults.CatalogModelId.Trim()
            : request.Catalog.ModelId.Trim();

        var displayName = string.IsNullOrWhiteSpace(request.Catalog.DisplayName)
            ? definition.Display.Name.Trim()
            : request.Catalog.DisplayName.Trim();

        return new CuratedImmutableOperationInput(
            DefinitionId: catalogId,
            DefinitionVersion: catalogVersion,
            CatalogModelId: catalogModelId,
            CatalogDisplayName: displayName,
            CatalogDescription: string.IsNullOrWhiteSpace(request.Catalog.Description)
                ? definition.Display.Description
                : request.Catalog.Description.Trim(),
            CatalogDisplayOrder: request.Catalog.DisplayOrder,
            CatalogIsActive: request.Catalog.IsActive,
            Repository: quantsAtCommit.Repository,
            RequestedRevision: quantsAtCommit.RequestedRevision,
            ResolvedRevision: clientRevision,
            QuantId: quant.Id,
            QuantLabel: quant.Label,
            ModelFiles: modelFiles,
            MmprojFiles: mmprojFiles,
            CompanionFiles: companionFiles,
            RouterModelId: defaults.RouterModelId.Trim(),
            TargetDirectory: defaults.TargetDirectory.Trim(),
            RouterPreset: routerPreset,
            SamplingParametersJson: samplingParametersJson,
            ReasoningChoicesJson: reasoningChoicesJson,
            ThinkingControlJson: thinkingControlJson,
            RequestFieldsWhenToolsPresentJson: requestFieldsWhenToolsPresentJson,
            CombineSystemAndDeveloperMessages: chatBehavior.CombineSystemAndDeveloperMessages,
            ThoughtBlockPattern: chatBehavior.ThoughtBlockPattern,
            ArtifactMetadata: artifactMetadata);
    }

    private static IReadOnlyList<CuratedArtifactMetadataInput> BuildArtifactMetadata(
        LlamaQuantGroupDto quant,
        LlamaProjectorArtifactDto? projector,
        IReadOnlyList<LlamaProjectorArtifactDto> companions)
    {
        var items = quant.Files
            .Select(f => new CuratedArtifactMetadataInput(
                Path: f.Path,
                Size: f.Size,
                Digest: f.GitOid ?? f.LfsOid,
                Etag: null))
            .ToList();

        if (projector is not null)
        {
            items.Add(new CuratedArtifactMetadataInput(
                Path: projector.Path,
                Size: projector.Size,
                Digest: projector.GitOid ?? projector.LfsOid,
                Etag: null));
        }

        foreach (var companion in companions)
        {
            items.Add(new CuratedArtifactMetadataInput(
                Path: companion.Path,
                Size: companion.Size,
                Digest: companion.GitOid ?? companion.LfsOid,
                Etag: null));
        }

        return items;
    }
}
