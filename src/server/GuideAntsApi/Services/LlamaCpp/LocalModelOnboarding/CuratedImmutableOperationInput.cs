using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public sealed record CuratedImmutableOperationInput(
    string DefinitionId,
    string DefinitionVersion,
    string CatalogModelId,
    string CatalogDisplayName,
    string? CatalogDescription,
    int? CatalogDisplayOrder,
    bool CatalogIsActive,
    string Repository,
    string RequestedRevision,
    string ResolvedRevision,
    string QuantId,
    string QuantLabel,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string> MmprojFiles,
    IReadOnlyList<string> CompanionFiles,
    string RouterModelId,
    string TargetDirectory,
    IReadOnlyDictionary<string, string> RouterPreset,
    string SamplingParametersJson,
    string? ReasoningChoicesJson,
    string ThinkingControlJson,
    string RequestFieldsWhenToolsPresentJson,
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    IReadOnlyList<CuratedArtifactMetadataInput>? ArtifactMetadata = null)
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ComputeHash()
    {
        var json = JsonSerializer.Serialize(this, CanonicalJsonOptions);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static CuratedImmutableOperationInput Deserialize(string json)
    {
        var parsed = JsonSerializer.Deserialize<CuratedImmutableOperationInput>(json, CanonicalJsonOptions);
        return parsed ?? throw new InvalidOperationException("Immutable operation input JSON is empty.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJsonOptions);

    public ExactStartModelDownloadRequest ToExactDownloadRequest(Guid operationId)
    {
        return new ExactStartModelDownloadRequest(
            OperationId: operationId.ToString("D"),
            Repository: Repository,
            ResolvedRevision: ResolvedRevision,
            ModelFiles: ModelFiles,
            MmprojFiles: MmprojFiles,
            CompanionFiles: CompanionFiles,
            Alias: RouterModelId,
            TargetDirectory: TargetDirectory,
            Preset: RouterPreset,
            PresetMode: "replace",
            ArtifactMetadata: ArtifactMetadata?
                .Select(a => new LlamaArtifactMetadataDto(a.Path, a.Size, a.Digest, a.Etag))
                .ToList());
    }
}

public sealed record CuratedArtifactMetadataInput(
    string Path,
    long? Size = null,
    string? Digest = null,
    string? Etag = null);
