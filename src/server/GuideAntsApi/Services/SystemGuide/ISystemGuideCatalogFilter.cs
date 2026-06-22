namespace GuideAntsApi.Services.SystemGuide;

/// <summary>
/// Identifies system-owned guide IDs that must not appear in global guide/template
/// catalog listings (D-GG-E). IDs come from <see cref="Settings.GuideAntsSystemSettings"/>.
/// </summary>
public interface ISystemGuideCatalogFilter
{
    Task<IReadOnlySet<Guid>> GetHiddenGuideIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns system guide IDs to exclude from catalog listings for the given project context.
    /// System guides remain visible when <paramref name="projectId"/> is the system project.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetHiddenGuideIdsForCatalogAsync(
        Guid? projectId,
        CancellationToken cancellationToken = default);
}
