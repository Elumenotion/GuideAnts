namespace GuideAntsApi.Settings;

/// <summary>
/// Authoritative in-process cache of <c>ChatDefaults</c> read from application settings (DB).
/// Chat resolution and toolbar readiness must use this instead of <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// </summary>
public interface IChatDefaultsStore
{
    ChatDefaultsSnapshot Current { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
