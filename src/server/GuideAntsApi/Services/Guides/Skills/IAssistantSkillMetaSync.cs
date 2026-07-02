using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides.Skills;

public interface IAssistantSkillMetaSync
{
    Task SyncAssistantAsync(Guid assistantId, CancellationToken cancellationToken = default);

    Task SyncFromSkillSavesAsync(
        Guid assistantId,
        IReadOnlyList<AssistantSkillSaveDto> skills,
        CancellationToken cancellationToken = default);

    Task UpsertFromManifestAsync(
        Guid assistantId,
        AssistantFile manifestFile,
        CancellationToken cancellationToken = default);
}
