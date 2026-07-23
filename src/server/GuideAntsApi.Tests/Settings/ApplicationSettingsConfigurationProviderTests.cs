using FluentAssertions;
using GuideAntsApi.Settings;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsConfigurationProviderTests
{
    [TestMethod]
    public void Load_WithEmptyConnectionString_DoesNotThrow()
    {
        var provider = new ApplicationSettingsConfigurationProvider(
            connectionString: string.Empty,
            registry: new SettingsSectionRegistry(),
            contentRootPath: AppContext.BaseDirectory,
            settingsSecrets: CreateValidSecretsOptions());

        Action act = () => provider.Load();

        act.Should().NotThrow();
    }

    [TestMethod]
    public void Load_WithInvalidConnectionString_ThrowsLoudStartupException()
    {
        var provider = new ApplicationSettingsConfigurationProvider(
            connectionString: "not-a-connection-string",
            registry: new SettingsSectionRegistry(),
            contentRootPath: AppContext.BaseDirectory,
            settingsSecrets: CreateValidSecretsOptions());

        Action act = () => provider.Load();

        act
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Failed to load DB-backed application settings*")
            .WithMessage("*Startup was aborted*");
    }

    [TestMethod]
    public void FlattenSection_EmitsEmptyCanonicalKeys_ForNullChatDefaultsFields()
    {
        var registry = new SettingsSectionRegistry();
        registry.TryGet("ChatDefaults", out var definition).Should().BeTrue();

        var provider = new ApplicationSettingsConfigurationProvider(
            connectionString: string.Empty,
            registry: registry,
            contentRootPath: AppContext.BaseDirectory,
            settingsSecrets: CreateValidSecretsOptions());

        var payload = new JsonObject
        {
            ["DefaultModelId"] = "gpt-5.5",
            ["OverrideAllChatModels"] = true,
            ["Temperature"] = null,
            ["TopP"] = null,
            ["ReasoningEffort"] = "low",
            ["SamplingParametersJson"] = null
        };

        provider.GetType()
            .GetMethod("FlattenSection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(provider, ["ChatDefaults", payload, definition]);

        provider.TryGet("ChatDefaults:Temperature", out var temperature).Should().BeTrue();
        temperature.Should().BeEmpty();
        provider.TryGet("ChatDefaults:TopP", out var topP).Should().BeTrue();
        topP.Should().BeEmpty();
        provider.TryGet("ChatDefaults:ReasoningEffort", out var reasoning).Should().BeTrue();
        reasoning.Should().Be("low");
    }

    [TestMethod]
    public void FlattenSection_DoesNotShadowMissingLlamaCppBaseUrl()
    {
        var registry = new SettingsSectionRegistry();
        registry.TryGet("LlamaCpp", out var definition).Should().BeTrue();

        var provider = new ApplicationSettingsConfigurationProvider(
            connectionString: string.Empty,
            registry: registry,
            contentRootPath: AppContext.BaseDirectory,
            settingsSecrets: CreateValidSecretsOptions());

        var payload = new JsonObject();

        provider.GetType()
            .GetMethod("FlattenSection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(provider, ["LlamaCpp", payload, definition]);

        provider.TryGet("LlamaCpp:BaseUrl", out _).Should().BeFalse();
    }

    private static SettingsSecretsOptions CreateValidSecretsOptions() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
        }
    };
}
