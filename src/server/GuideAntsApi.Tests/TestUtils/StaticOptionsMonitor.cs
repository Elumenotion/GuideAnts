using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.TestUtils;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> returning a fixed value for tests.
/// </summary>
internal sealed class StaticOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
{
    public TOptions CurrentValue { get; } = value;

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<TOptions, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
