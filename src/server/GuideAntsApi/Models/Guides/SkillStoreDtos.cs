namespace GuideAntsApi.Models.Guides;

/// <summary>Catalog list row.</summary>
public record SkillCatalogEntryDto(
    Guid Id,
    string Name,
    string Description,
    int CurrentVersion,
    int FileCount,
    DateTime Updated);

public record SkillCatalogFileDto(
    string RelativePath,
    string? ContentType,
    int SizeBytes);

public record SkillCatalogDetailDto(
    Guid Id,
    string Name,
    string Description,
    int CurrentVersion,
    string ContentHash,
    List<string> RequiresToolsets,
    List<string> RequiresTools,
    List<string> FallbackForToolsets,
    List<string> FallbackForTools,
    List<string> Platforms,
    List<SkillCatalogFileDto> Files,
    List<int> Versions,
    DateTime Updated);

public record SkillPublishRequest(Guid AssistantId, string SkillName);

public record SkillPublishResultDto(Guid SkillId, string Name, int Version, bool CreatedNewSkill);

public record SkillInstallRequest(string SkillName, int? Version, bool Overwrite);

public record SkillUpdateFromStoreRequest(int? Version);

public record SkillCatalogPackageFileDto(
    string RelativePath,
    string? ContentType,
    string ContentBase64);

/// <summary>Whole snapshot in one response so the builder can stage an install without
/// one request per file.</summary>
public record SkillCatalogPackageDto(
    string Name,
    int Version,
    List<SkillCatalogPackageFileDto> Files);
