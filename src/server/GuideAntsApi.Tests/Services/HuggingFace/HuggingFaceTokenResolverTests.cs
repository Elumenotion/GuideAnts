using FluentAssertions;
using GuideAntsApi.Services.HuggingFace;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Services.HuggingFace;

/// <summary>
/// The resolver is intentionally trivial: it reads
/// <c>HuggingFace:Token</c> from <see cref="IConfiguration"/> and trims it.
/// The DB-backed <c>ApplicationSettingsConfigurationProvider</c> is what
/// surfaces the stored value as configuration; these tests stub the
/// configuration directly so they exercise only the resolver contract.
/// </summary>
[TestClass]
public sealed class HuggingFaceTokenResolverTests
{
    [TestMethod]
    public void Resolve_ReturnsTrimmedConfiguredValue()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["HuggingFace:Token"] = "   hf_abc123   ",
        });
        var resolver = new HuggingFaceTokenResolver(configuration);

        resolver.Resolve().Should().Be("hf_abc123");
    }

    [TestMethod]
    public void Resolve_ReturnsNull_WhenNotConfigured()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>());
        var resolver = new HuggingFaceTokenResolver(configuration);

        resolver.Resolve().Should().BeNull();
    }

    [TestMethod]
    public void Resolve_ReturnsNull_WhenValueIsWhitespace()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["HuggingFace:Token"] = "   ",
        });
        var resolver = new HuggingFaceTokenResolver(configuration);

        resolver.Resolve().Should().BeNull();
    }

    [TestMethod]
    public void Resolve_DoesNotReadFromLegacyLlamaCppHfToken()
    {
        // R-7.4 (rewritten): there is ONE token key, HuggingFace:Token. The
        // LlamaCpp:HfToken slot no longer exists as a source; the runtime
        // resolver never falls through to the old key.
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["LlamaCpp:HfToken"] = "stale_value",
        });
        var resolver = new HuggingFaceTokenResolver(configuration);

        resolver.Resolve().Should().BeNull();
    }

    [TestMethod]
    public void Resolve_DoesNotReadFromHfTokenEnvKey()
    {
        // HF_TOKEN is not a runtime source. Reading it directly would be a
        // silent secondary source and is exactly the pattern the single-token
        // contract forbids.
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["HF_TOKEN"] = "env_value",
        });
        var resolver = new HuggingFaceTokenResolver(configuration);

        resolver.Resolve().Should().BeNull();
    }

    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
