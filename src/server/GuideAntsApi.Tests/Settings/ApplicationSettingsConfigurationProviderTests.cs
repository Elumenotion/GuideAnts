using FluentAssertions;
using GuideAntsApi.Settings;

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

    private static SettingsSecretsOptions CreateValidSecretsOptions() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
        }
    };
}
