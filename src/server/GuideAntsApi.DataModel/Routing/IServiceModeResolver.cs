namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Resolves a <see cref="ServiceMode"/> for one of the five non-chat services
/// (see <see cref="RoutedServiceNames"/>). The caller supplies the service name
/// (e.g. "Embeddings") plus an optional mode id; when the mode id is null/blank
/// the service's default mode is used.
/// </summary>
public interface IServiceModeResolver
{
    Task<ServiceMode> ResolveAsync(string serviceName, string? modeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceMode>> GetModesAsync(string serviceName, CancellationToken cancellationToken = default);
}
