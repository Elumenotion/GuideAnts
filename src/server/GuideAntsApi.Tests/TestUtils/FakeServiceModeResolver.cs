using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.TestUtils;

/// <summary>
/// Test fake for <see cref="IServiceModeResolver"/> that returns a preconfigured
/// <see cref="ServiceMode"/> per service name. Use to drive provider-routed
/// services from unit tests without the full database-backed resolver.
/// </summary>
internal sealed class FakeServiceModeResolver : IServiceModeResolver
{
    private readonly Dictionary<string, ServiceMode> _modesByService;

    public FakeServiceModeResolver(params (string ServiceName, ServiceMode Mode)[] modes)
    {
        _modesByService = modes.ToDictionary(
            m => m.ServiceName,
            m => m.Mode,
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
        if (!_modesByService.TryGetValue(serviceName, out var mode))
        {
            throw RoutingException.ModeNotFound(serviceName, modeId ?? "default");
        }

        return Task.FromResult(mode);
    }

    public Task<IReadOnlyList<ServiceMode>> GetModesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (!_modesByService.TryGetValue(serviceName, out var mode))
        {
            return Task.FromResult<IReadOnlyList<ServiceMode>>(Array.Empty<ServiceMode>());
        }

        return Task.FromResult<IReadOnlyList<ServiceMode>>(new[] { mode });
    }
}
