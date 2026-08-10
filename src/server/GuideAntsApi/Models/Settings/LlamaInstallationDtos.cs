namespace GuideAntsApi.Models.Settings;

public sealed record InstallationArtifactDto(
    string RepositoryPath,
    string InstalledRelativePath,
    long? ByteSize = null,
    string? Digest = null,
    string? Etag = null);

public sealed record LlamaInstallationDetailDto(
    string ModelId,
    SettingsModelDto CatalogModel,
    string? CatalogId,
    string? CatalogVersion,
    string? Repository,
    string? RequestedRevision,
    string? ResolvedRevision,
    string? QuantId,
    string? QuantLabel,
    string RouterModelId,
    string TargetDirectory,
    IReadOnlyList<InstallationArtifactDto> ModelArtifacts,
    IReadOnlyList<InstallationArtifactDto> ProjectorArtifacts,
    IReadOnlyDictionary<string, string> RouterPresetSnapshot,
    string RuntimeState,
    bool Loaded,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record ChangeQuantRequestDto(
    string QuantId,
    string ResolvedRevision);

public sealed record RepairInstallationRequestDto(
    bool Confirm = true);

public sealed record AdoptInstallationRequestDto(
    string CatalogId,
    string CatalogVersion,
    bool Confirm = false);

public sealed record AdoptDiffFieldDto(
    string Field,
    string? CurrentValue,
    string? CuratedValue,
    bool Verifiable,
    string? RequiredAction);

public sealed record AdoptPreviewResponseDto(
    string ModelId,
    string CatalogId,
    string CatalogVersion,
    IReadOnlyList<AdoptDiffFieldDto> Differences,
    bool CanAdopt,
    IReadOnlyList<string> Blockers);

public sealed record LifecycleOperationResponseDto(
    string OperationId,
    string Status);

public sealed record AddModelInstallHuggingFaceExplicitDto(
    string Repository,
    string ResolvedRevision,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string>? MmprojFiles,
    string TargetDirectory,
    IReadOnlyDictionary<string, string> RouterPreset);
