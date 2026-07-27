namespace GuideAntsApi.Settings;

/// <summary>
/// Authoritative reader of <c>ChatDefaults</c> from application settings (DB).
/// Chat resolution and toolbar readiness must use this instead of
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// Implementations must not serve a process-local snapshot that can diverge across replicas.
/// </summary>
public interface IChatDefaultsStore
{
    ChatDefaultsSnapshot Current { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
