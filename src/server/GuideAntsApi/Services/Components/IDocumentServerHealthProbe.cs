namespace GuideAntsApi.Services.Components;

/// <summary>
/// Answers whether the DocumentServer container is responding right now, not just enabled in config.
/// </summary>
public interface IDocumentServerHealthProbe
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}
