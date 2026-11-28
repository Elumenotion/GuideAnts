using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Guides.Skills;

internal static class CatalogVersionLookup
{
    /// <summary>
    /// Newest published version per catalog entry name, for the skill names an assistant actually
    /// has pinned. One small query so the builder can offer an update without a per-skill lookup.
    /// Returns an empty map when nothing is pinned.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> ResolveForAsync(
        ApplicationDbContext context,
        IEnumerable<AssistantSkillMeta> metas,
        CancellationToken cancellationToken = default)
    {
        var names = metas
            .Where(m => m.SkillId is not null && m.CatalogVersion is not null)
            .Select(m => m.SkillName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return result;
        }

        var skills = await context.Skills
            .AsNoTracking()
            .Include(s => s.Versions)
            .Where(s => names.Contains(s.Name))
            .ToListAsync(cancellationToken);

        foreach (var skill in skills)
        {
            var current = skill.CurrentSnapshot();
            if (current is not null)
            {
                result[skill.Name] = current.Version;
            }
        }

        return result;
    }
}
