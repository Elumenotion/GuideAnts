using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Skills;

/// <summary>
/// Global skill catalog (skill store): publishes saved assistant skills as immutable
/// catalog versions and serves the catalog browse/detail read model.
/// </summary>
public interface ISkillCatalogService
{
    Task<IReadOnlyList<SkillCatalogEntryDto>> ListAsync(string? search, CancellationToken cancellationToken = default);

    Task<SkillCatalogDetailDto?> GetAsync(string skillName, int? version, CancellationToken cancellationToken = default);

    /// <summary>Full snapshot bytes for staging an install client-side.</summary>
    Task<SkillCatalogPackageDto?> GetPackageAsync(string skillName, int? version, CancellationToken cancellationToken = default);

    Task<SkillPublishResultDto> PublishFromAssistantAsync(
        Guid assistantId,
        string skillName,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the number of installs left un-pinned, or null when the entry does not exist.</summary>
    Task<int?> DeleteAsync(string skillName, CancellationToken cancellationToken = default);
}
