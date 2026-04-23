using FluentAssertions;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Settings;

/// <summary>
/// Phase E (R-5.7): verify <see cref="RuntimeDependencySourceResolver"/> classifies
/// providers into the stable source labels the Infrastructure tab renders as
/// badges.
/// </summary>
[TestClass]
public sealed class RuntimeDependencySourceResolutionTests
{
    [TestMethod]
    public void Resolve_WithInMemorySourceOnly_ReturnsUnknown()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110"
            })
            .Build();

        var source = RuntimeDependencySourceResolver.Resolve(
            configuration,
            "LocalServiceHosts:EmbeddingsBaseUrl");

        source.Should().Be(RuntimeDependencySourceResolver.SourceUnknown);
    }

    [TestMethod]
    public void Resolve_WithEnvironmentVariables_ReturnsEnvironment()
    {
        const string envKey = "LocalServiceHosts__ProbeEnvKey";
        Environment.SetEnvironmentVariable(envKey, "http://env-host:1234");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var source = RuntimeDependencySourceResolver.Resolve(
                configuration,
                "LocalServiceHosts:ProbeEnvKey");

            source.Should().Be(RuntimeDependencySourceResolver.SourceEnvironment);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [TestMethod]
    public void Resolve_WithAppsettingsJsonFile_ReturnsAppSettings()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "guideants-e1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var appsettingsPath = Path.Combine(tempDirectory, "appsettings.test.json");

        try
        {
            File.WriteAllText(
                appsettingsPath,
                "{ \"LocalServiceHosts\": { \"EmbeddingsBaseUrl\": \"http://file-host:8110\" } }");

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(appsettingsPath, optional: false)
                .Build();

            var source = RuntimeDependencySourceResolver.Resolve(
                configuration,
                "LocalServiceHosts:EmbeddingsBaseUrl");

            source.Should().Be(RuntimeDependencySourceResolver.SourceAppSettings);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Resolve_LastProviderWins_WhenMultipleClaimTheSameKey()
    {
        const string envKey = "LocalServiceHosts__OverrideKey";
        Environment.SetEnvironmentVariable(envKey, "http://env-host:1234");
        try
        {
            // AddInMemoryCollection first, then AddEnvironmentVariables — env must win.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LocalServiceHosts:OverrideKey"] = "http://in-memory:8110"
                })
                .AddEnvironmentVariables()
                .Build();

            var source = RuntimeDependencySourceResolver.Resolve(
                configuration,
                "LocalServiceHosts:OverrideKey");

            source.Should().Be(RuntimeDependencySourceResolver.SourceEnvironment);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }
}
