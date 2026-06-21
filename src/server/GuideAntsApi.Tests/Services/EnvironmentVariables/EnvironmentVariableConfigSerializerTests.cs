using FluentAssertions;
using GuideAntsApi.Models;
using GuideAntsApi.Services.EnvironmentVariables;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Tests.Services.EnvironmentVariables;

[TestClass]
public sealed class EnvironmentVariableConfigSerializerTests
{
    [TestMethod]
    public void DeserializeForClient_Masks_secret_values()
    {
        var json = EnvironmentVariableConfigSerializer.SerializeFromClient([
            new EnvironmentVariableDto("PUBLIC_VALUE", "plain", false),
            new EnvironmentVariableDto("SECRET_VALUE", "top-secret", true)
        ], null, TestSecretsOptions);

        var variables = EnvironmentVariableConfigSerializer.DeserializeForClient(json);

        json.Should().NotContain("top-secret");
        json.Should().Contain("encv2::");
        variables.Should().ContainEquivalentOf(new EnvironmentVariableDto("PUBLIC_VALUE", "plain", false));
        variables.Should().ContainEquivalentOf(new EnvironmentVariableDto(
            "SECRET_VALUE",
            EnvironmentVariableConfigSerializer.MaskedSecretValue,
            true));
    }

    [TestMethod]
    public void SerializeFromClient_Preserves_masked_secret_values()
    {
        var existing = EnvironmentVariableConfigSerializer.SerializeFromClient([
            new EnvironmentVariableDto("SECRET_VALUE", "original-secret", true)
        ], null, TestSecretsOptions);

        var updated = EnvironmentVariableConfigSerializer.SerializeFromClient([
            new EnvironmentVariableDto(
                "SECRET_VALUE",
                EnvironmentVariableConfigSerializer.MaskedSecretValue,
                true)
        ], existing, TestSecretsOptions);

        var executionEnvironment = EnvironmentVariableConfigSerializer.DeserializeForExecution(TestSecretsOptions, updated);

        executionEnvironment.Should().ContainKey("SECRET_VALUE")
            .WhoseValue.Should().Be("original-secret");
    }

    [TestMethod]
    public void SerializeFromClient_Rejects_reserved_names()
    {
        Action act = () => EnvironmentVariableConfigSerializer.SerializeFromClient([
            new EnvironmentVariableDto("PATH", "/tmp/bin", false)
        ], null, TestSecretsOptions);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*reserved*");
    }

    [TestMethod]
    public void DeserializeForExecution_Applies_later_crew_manifests_as_overrides()
    {
        var guide = EnvironmentVariableConfigSerializer.SerializeFromClient([
            new EnvironmentVariableDto("SHARED_VALUE", "guide", false),
            new EnvironmentVariableDto("GUIDE_ONLY", "yes", false)
        ], null, TestSecretsOptions);
        var crewAssistant = EnvironmentVariableConfigSerializer.SerializeFromClient([
            new EnvironmentVariableDto("SHARED_VALUE", "crew", false)
        ], null, TestSecretsOptions);

        var environment = EnvironmentVariableConfigSerializer.DeserializeForExecution(TestSecretsOptions, guide, crewAssistant);

        environment.Should().ContainKey("SHARED_VALUE").WhoseValue.Should().Be("crew");
        environment.Should().ContainKey("GUIDE_ONLY").WhoseValue.Should().Be("yes");
    }

    private static SettingsSecretsOptions TestSecretsOptions => new()
    {
        ActiveKeyId = "test",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["test"] = Convert.ToBase64String(new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16,
                17, 18, 19, 20, 21, 22, 23, 24,
                25, 26, 27, 28, 29, 30, 31, 32
            })
        }
    };
}
