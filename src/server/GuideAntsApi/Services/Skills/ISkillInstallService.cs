using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Skills;

/// <summary>
/// Installs skill catalog entries into an assistant as ordinary local skill packages
/// (AssistantFile rows + a pinned AssistantSkillMeta row), so the runtime, materializer and
/// MCP exposure need no catalog awareness.
/// </summary>
public interface ISkillInstallService
{
    Task<AssistantSkillDto> InstallAsync(
        Guid assistantId,
        SkillInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<AssistantSkillDto> UpdateFromCatalogAsync(
        Guid assistantId,
        string skillName,
        SkillUpdateFromStoreRequest request,
        CancellationToken cancellationToken = default);
}
