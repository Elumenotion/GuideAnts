using GuideAntsApi.DataModel;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SystemGuide;

public sealed class SystemGuideCatalogFilter(
    IGuideAntsSystemSettingsStore settingsStore,
    ApplicationDbContext dbContext) : ISystemGuideCatalogFilter
{
    public async Task<IReadOnlySet<Guid>> GetHiddenGuideIdsForCatalogAsync(
        Guid? projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId.HasValue)
        {
            var isSystemProject = await dbContext.Projects
                .AsNoTracking()
                .AnyAsync(
                    p => p.Id == projectId.Value && !p.Deleted && p.IsSystemProject,
                    cancellationToken);

            if (isSystemProject)
            {
                return Empty;
            }
        }

        return await GetHiddenGuideIdsAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> GetHiddenGuideIdsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (settings is null)
        {
            return Empty;
        }

        var ids = new HashSet<Guid>();
        if (settings.UserGuideId is Guid userGuideId)
        {
            ids.Add(userGuideId);
        }

        if (settings.AdminGuideId is Guid adminGuideId)
        {
            ids.Add(adminGuideId);
        }

        return ids.Count == 0 ? Empty : ids;
    }

    private static readonly HashSet<Guid> Empty = [];
}
