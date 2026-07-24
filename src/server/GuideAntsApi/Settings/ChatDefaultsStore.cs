namespace GuideAntsApi.Settings;

public sealed class ChatDefaultsStore : IChatDefaultsStore
{
    public const string SectionName = "ChatDefaults";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _sync = new();
    private ChatDefaultsSnapshot _current = ChatDefaultsSnapshot.Empty;

    public ChatDefaultsStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public ChatDefaultsSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        var section = await settings.GetSectionAsync(SectionName, cancellationToken).ConfigureAwait(false);
        var snapshot = ChatDefaultsSnapshot.FromSection(section);

        lock (_sync)
        {
            _current = snapshot;
        }
    }
}
