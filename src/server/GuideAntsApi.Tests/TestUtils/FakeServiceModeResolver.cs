using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.TestUtils;

/// <summary>
/// Test fake for <see cref="IServiceModeResolver"/> that returns preconfigured
/// <see cref="ServiceMode"/> rows per service name. Use to drive provider-routed
/// services from unit tests without the full database-backed resolver.
/// </summary>
internal sealed class FakeServiceModeResolver : IServiceModeResolver
{
    private readonly Dictionary<string, IReadOnlyList<ServiceMode>> _modesByService;

    public FakeServiceModeResolver(params (string ServiceName, ServiceMode Mode)[] modes)
    {
        _modesByService = modes
            .GroupBy(m => m.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ServiceMode>)g.Select(x => x.Mode).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Convenience constructor for single-service tests.
    /// </summary>
    public FakeServiceModeResolver(string serviceName, string providerSection, string modeId = "default")
        : this((serviceName, new ServiceMode(
            ModeId: modeId,
            ProviderSection: providerSection,
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true)))
    {
    }

    public Task<ServiceMode> ResolveAsync(string serviceName, string? modeId, CancellationToken cancellationToken = default)
    {
        if (!_modesByService.TryGetValue(serviceName, out var modes) || modes.Count == 0)
        {
            throw RoutingException.ModeNotFound(serviceName, modeId ?? "default");
        }

        if (string.IsNullOrWhiteSpace(modeId))
        {
            var selected = modes.FirstOrDefault(m => m.IsDefault && m.Enabled)
                ?? modes.FirstOrDefault(m => m.IsDefault)
                ?? modes[0];
            return Task.FromResult(selected);
        }

        var explicitMode = modes.FirstOrDefault(m => string.Equals(m.ModeId, modeId, StringComparison.Ordinal))
            ?? throw RoutingException.ModeNotFound(serviceName, modeId);
        return Task.FromResult(explicitMode);
    }

    public Task<IReadOnlyList<ServiceMode>> GetModesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (!_modesByService.TryGetValue(serviceName, out var modes))
        {
            return Task.FromResult<IReadOnlyList<ServiceMode>>(Array.Empty<ServiceMode>());
        }

        return Task.FromResult(modes);
    }
}
