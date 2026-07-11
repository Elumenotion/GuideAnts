using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public static class InstallationArtifactRecords
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<InstallationArtifactDto> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<InstallationArtifactDto>();
        }

        try
        {
            var nodes = JsonSerializer.Deserialize<JsonArray>(json, JsonOptions);
            if (nodes is null || nodes.Count == 0)
            {
                return Array.Empty<InstallationArtifactDto>();
            }

            var artifacts = new List<InstallationArtifactDto>(nodes.Count);
            foreach (var node in nodes)
            {
                if (node is not JsonObject obj)
                {
                    continue;
                }

                artifacts.Add(new InstallationArtifactDto(
                    RepositoryPath: obj["repositoryPath"]?.GetValue<string>() ?? string.Empty,
                    InstalledRelativePath: obj["installedRelativePath"]?.GetValue<string>() ?? string.Empty,
                    ByteSize: obj["byteSize"]?.GetValue<long?>(),
                    Digest: obj["digest"]?.GetValue<string>(),
                    Etag: obj["etag"]?.GetValue<string>()));
            }

            return artifacts;
        }
        catch (JsonException)
        {
            return Array.Empty<InstallationArtifactDto>();
        }
    }

    public static string Serialize(IEnumerable<InstallationArtifactDto> artifacts)
    {
        var nodes = artifacts.Select(a => new JsonObject
        {
            ["repositoryPath"] = a.RepositoryPath,
            ["installedRelativePath"] = a.InstalledRelativePath,
            ["byteSize"] = a.ByteSize,
            ["digest"] = a.Digest,
            ["etag"] = a.Etag,
        }).ToList<JsonNode?>();

        return new JsonArray(nodes.ToArray()).ToJsonString(JsonOptions);
    }

    public static string SerializeFromPaths(
        string targetDirectory,
        IReadOnlyList<string> repositoryPaths,
        IReadOnlyList<LlamaArtifactMetadataDto>? metadata = null)
    {
        var artifacts = repositoryPaths.Select(path => new InstallationArtifactDto(
            RepositoryPath: path,
            InstalledRelativePath: $"{targetDirectory.Trim().Trim('/')}/{Path.GetFileName(path.Replace('\\', '/'))}",
            ByteSize: metadata?.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal))?.Size,
            Digest: metadata?.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal))?.Digest,
            Etag: metadata?.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal))?.Etag));

        return Serialize(artifacts);
    }

    public static IReadOnlyDictionary<string, string> ParsePresetSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return parsed ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
