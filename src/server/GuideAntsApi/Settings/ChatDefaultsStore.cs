namespace GuideAntsApi.Settings;

/// <summary>
/// Reads <c>ChatDefaults</c> from application settings (DB) on every access.
/// Process-local caching is intentionally not used: Azure scales the API to multiple
/// replicas, and a warmed singleton would leave non-writing replicas with an empty snapshot
/// after another replica persisted a successful Settings save.
/// </summary>
public sealed class ChatDefaultsStore : IChatDefaultsStore
{
    public const string SectionName = "ChatDefaults";

    private readonly IServiceScopeFactory _scopeFactory;

    public ChatDefaultsStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public ChatDefaultsSnapshot Current
    {
        get
        {
            // ASP.NET Core has no SynchronizationContext; ConfigureAwait(false) avoids
            // capturing a request context if one is introduced later.
            return LoadSnapshotAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Kept for startup/settings-save call sites. Current always loads from DB, so this
        // does not warm a cache — it verifies the section is readable.
        return LoadSnapshotAsync(cancellationToken);
    }

    private async Task<ChatDefaultsSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        var section = await settings.GetSectionAsync(SectionName, cancellationToken).ConfigureAwait(false);
        return ChatDefaultsSnapshot.FromSection(section);
    }
}
