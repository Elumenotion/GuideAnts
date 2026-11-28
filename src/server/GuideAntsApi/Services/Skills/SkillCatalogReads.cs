using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Skills;

public static class SkillCatalogReads
{
    /// <summary>
    /// Resolves the current snapshot for a catalog entry from its loaded <see cref="Skill.Versions"/>
    /// collection. <see cref="Skill.CurrentVersionId"/> is a maintained pointer rather than a
    /// foreign key, so reads go through the versions they already have. Falls back to the highest
    /// version if the pointer is stale or null.
    /// </summary>
    public static SkillVersion? CurrentSnapshot(this Skill skill) =>
        (skill.CurrentVersionId is null
            ? null
            : skill.Versions.FirstOrDefault(v => v.Id == skill.CurrentVersionId))
        ?? skill.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
}
