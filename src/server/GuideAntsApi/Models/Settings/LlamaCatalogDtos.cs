using System.Text.Json;

namespace GuideAntsApi.Models.Settings;

public sealed record LlamaCatalogDisplayDto(
    string Name,
    string Description,
    IReadOnlyList<string> Labels,
    string License,
    string DocumentationUrl,
    IReadOnlyList<string>? RecommendedQuantLabels = null,
    bool? PrimaryRecommendation = null);

public sealed record LlamaCatalogSourceDto(
    string Repository,
    string? Revision = null);

public sealed record LlamaCatalogMmprojDto(
    string Path,
    string? Repository = null,
    string? Revision = null);

public sealed record LlamaCatalogCompanionArtifactDto(
    string Path,
    string? Repository = null,
    string? Revision = null);

public sealed record LlamaCatalogChatBehaviorDto(
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    JsonElement SamplingParametersJson,
    JsonElement ThinkingControlJson,
    JsonElement? RequestFieldsWhenToolsPresent);

public sealed record LlamaCatalogDefaultsDto(
    string CatalogModelId,
    string RouterModelId,
    string TargetDirectory,
    LlamaCatalogMmprojDto? Mmproj,
    IReadOnlyList<LlamaCatalogCompanionArtifactDto>? CompanionArtifacts,
    IReadOnlyDictionary<string, string> RouterPreset,
    LlamaCatalogChatBehaviorDto ChatBehavior);

public sealed record LlamaCatalogQuantGuidanceDto(
    string Summary);

public sealed record LlamaCatalogQuantMetadataDto(
    IReadOnlyList<string>? RecommendedLabels = null,
    IReadOnlyDictionary<string, LlamaCatalogQuantGuidanceDto>? Guidance = null);

public sealed record LlamaCatalogHardwareNotesDto(
    string Summary,
    string ContextClass);

public sealed record LlamaCatalogDefinitionDto(
    string Id,
    LlamaCatalogDisplayDto Display,
    LlamaCatalogSourceDto Source,
    LlamaCatalogDefaultsDto Defaults,
    LlamaCatalogQuantMetadataDto QuantMetadata,
    LlamaCatalogHardwareNotesDto HardwareNotes);

public sealed record LlamaCatalogResponseDto(
    int SchemaVersion,
    string Task,
    string CatalogVersion,
    IReadOnlyList<LlamaCatalogDefinitionDto> Models);

public sealed record LlamaQuantArtifactDto(
    string Path,
    long? Size,
    int? ShardIndex = null,
    int? ShardCount = null,
    string? LfsOid = null,
    string? GitOid = null);

public sealed record LlamaQuantGuidanceDto(
    string Summary);

public sealed record LlamaQuantGroupDto(
    string Id,
    string Label,
    long TotalBytes,
    IReadOnlyList<LlamaQuantArtifactDto> Files,
    LlamaQuantGuidanceDto? Guidance = null);

public sealed record LlamaProjectorArtifactDto(
    string Path,
    long? Size,
    string? LfsOid = null,
    string? GitOid = null);

public sealed record LlamaCatalogQuantsResponseDto(
    string CatalogId,
    string Repository,
    string RequestedRevision,
    string ResolvedRevision,
    IReadOnlyList<LlamaQuantGroupDto> Quants,
    LlamaProjectorArtifactDto? Projector,
    IReadOnlyList<LlamaProjectorArtifactDto> Companions);

public sealed class LlamaCatalogServiceException : Exception
{
    public LlamaCatalogServiceException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}
