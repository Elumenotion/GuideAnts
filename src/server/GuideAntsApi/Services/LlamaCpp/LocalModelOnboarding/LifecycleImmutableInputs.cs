using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public static class LocalModelOperationKinds
{
    public const string CuratedInstall = "curatedInstall";
    public const string ChangeQuant = "changeQuant";
    public const string Repair = "repair";
    public const string CustomInstall = "customInstall";
}

public sealed record ChangeQuantImmutableInput(
    string ModelId,
    string CatalogId,
    string CatalogVersion,
    string OldQuantId,
    string NewQuantId,
    string NewQuantLabel,
    string Repository,
    string ResolvedRevision,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string> MmprojFiles,
    string RouterModelId,
    string RuntimeProfileId,
    string TargetDirectory,
    IReadOnlyDictionary<string, string> RouterPreset,
    IReadOnlyList<string> ObsoleteRepositoryPaths,
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

    public static ChangeQuantImmutableInput Deserialize(string json)
    {
        var parsed = JsonSerializer.Deserialize<ChangeQuantImmutableInput>(json, CanonicalJsonOptions);
        return parsed ?? throw new InvalidOperationException("Change quant immutable input JSON is empty.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJsonOptions);

    public ExactStartModelDownloadRequest ToExactDownloadRequest(Guid operationId) =>
        new(
            OperationId: operationId.ToString("D"),
            Repository: Repository,
            ResolvedRevision: ResolvedRevision,
            ModelFiles: ModelFiles,
            MmprojFiles: MmprojFiles,
            Alias: RouterModelId,
            TargetDirectory: TargetDirectory,
            Preset: RouterPreset,
            PresetMode: "replace",
            ArtifactMetadata: ArtifactMetadata?
                .Select(a => new LlamaArtifactMetadataDto(a.Path, a.Size, a.Digest, a.Etag))
                .ToList());
}

public sealed record RepairImmutableInput(
    string ModelId,
    string Repository,
    string ResolvedRevision,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string> MmprojFiles,
    string RouterModelId,
    string RuntimeProfileId,
    string TargetDirectory,
    IReadOnlyDictionary<string, string> RouterPreset,
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

    public static RepairImmutableInput Deserialize(string json)
    {
        var parsed = JsonSerializer.Deserialize<RepairImmutableInput>(json, CanonicalJsonOptions);
        return parsed ?? throw new InvalidOperationException("Repair immutable input JSON is empty.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJsonOptions);

    public ExactStartModelDownloadRequest ToExactDownloadRequest(Guid operationId) =>
        new(
            OperationId: operationId.ToString("D"),
            Repository: Repository,
            ResolvedRevision: ResolvedRevision,
            ModelFiles: ModelFiles,
            MmprojFiles: MmprojFiles,
            Alias: RouterModelId,
            TargetDirectory: TargetDirectory,
            Preset: RouterPreset,
            PresetMode: "replace",
            ArtifactMetadata: ArtifactMetadata?
                .Select(a => new LlamaArtifactMetadataDto(a.Path, a.Size, a.Digest, a.Etag))
                .ToList());
}

public sealed record CustomInstallImmutableInput(
    string CatalogModelId,
    string CatalogDisplayName,
    string? CatalogDescription,
    int? CatalogDisplayOrder,
    bool CatalogIsActive,
    string Repository,
    string RequestedRevision,
    string ResolvedRevision,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string> MmprojFiles,
    string RouterModelId,
    string RuntimeProfileId,
    string TargetDirectory,
    IReadOnlyDictionary<string, string> RouterPreset,
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

    public static CustomInstallImmutableInput Deserialize(string json)
    {
        var parsed = JsonSerializer.Deserialize<CustomInstallImmutableInput>(json, CanonicalJsonOptions);
        return parsed ?? throw new InvalidOperationException("Custom install immutable input JSON is empty.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJsonOptions);

    public ExactStartModelDownloadRequest ToExactDownloadRequest(Guid operationId) =>
        new(
            OperationId: operationId.ToString("D"),
            Repository: Repository,
            ResolvedRevision: ResolvedRevision,
            ModelFiles: ModelFiles,
            MmprojFiles: MmprojFiles,
            Alias: RouterModelId,
            TargetDirectory: TargetDirectory,
            Preset: RouterPreset,
            PresetMode: "replace",
            ArtifactMetadata: ArtifactMetadata?
                .Select(a => new LlamaArtifactMetadataDto(a.Path, a.Size, a.Digest, a.Etag))
                .ToList());
}
