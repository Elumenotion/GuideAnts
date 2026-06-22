namespace GuideAntsApi.Settings;

/// <summary>
/// Typed view of the <c>GuideAntsSystem</c> application-settings section (D-GG-I).
/// IDs are generated at seed time; runtime reads them from settings — never hard-coded.
/// </summary>
public sealed class GuideAntsSystemSettings
{
    public const string SectionName = "GuideAntsSystem";
    public const string DefaultClientBridgeId = "guideants-app";

    public Guid? ProjectId { get; init; }
    public Guid? UserGuideId { get; init; }
    public Guid? AdminGuideId { get; init; }
    public Guid? UserNotebookId { get; init; }
    public Guid? AdminNotebookId { get; init; }
    public Guid? UserPublishedGuideId { get; init; }
    public Guid? AdminPublishedGuideId { get; init; }
    public string ClientBridgeId { get; init; } = DefaultClientBridgeId;

    public bool IsFullySeeded =>
        ProjectId.HasValue
        && UserGuideId.HasValue
        && AdminGuideId.HasValue
        && UserNotebookId.HasValue
        && AdminNotebookId.HasValue
        && UserPublishedGuideId.HasValue
        && AdminPublishedGuideId.HasValue;
}

public interface IGuideAntsSystemSettingsStore
{
    Task<GuideAntsSystemSettings?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GuideAntsSystemSettings settings, CancellationToken cancellationToken = default);
}
