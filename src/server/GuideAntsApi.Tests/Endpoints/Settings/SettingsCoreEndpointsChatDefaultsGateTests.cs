using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Tests.Endpoints.Settings;

///<summary>
/// The chat-defaults PUT decides whether to contact the llama runtime from these two
/// projected fields. A projection that erases the default model id when override-all is
/// off (an earlier shape of this helper) silently disables the load side of the rule,
/// because the endpoint then never calls the reconciler at all.
///</summary>
[TestClass]
public sealed class SettingsCoreEndpointsChatDefaultsGateTests
{
    private static SettingsSectionDto Section(string? defaultModelId, bool overrideAll, double? temperature = null)
    {
        var payload = new JsonObject
        {
            ["DefaultModelId"] = defaultModelId is null ? null : JsonValue.Create(defaultModelId),
            ["OverrideAllChatModels"] = JsonValue.Create(overrideAll),
        };
        if (temperature is not null)
        {
            payload["Temperature"] = JsonValue.Create(temperature.Value);
        }

        return new SettingsSectionDto(
            SectionName: "ChatDefaults",
            SchemaVersion: 1,
            RowVersion: "1",
            UpdatedUtc: DateTime.UtcNow,
            Payload: payload,
            SecretHasValue: new Dictionary<string, bool>());
    }

    [TestMethod]
    public void OverrideOff_DefaultModelIdStillProjects_SoTheLoadSideCanFire()
    {
        var before = SettingsCoreEndpoints.LlamaRelevantChatDefaults(Section("muse-max", overrideAll: false));
        var after = SettingsCoreEndpoints.LlamaRelevantChatDefaults(Section("qwen-a", overrideAll: false));

        before.DefaultModelId.Should().Be("muse-max");
        before.OverrideAllChatModels.Should().BeFalse();
        after.DefaultModelId.Should().Be("qwen-a");
        after.OverrideAllChatModels.Should().BeFalse();
    }

    [TestMethod]
    public void TemperatureOnlyChange_ProjectionUnchanged_SoNothingFires()
    {
        var before = SettingsCoreEndpoints.LlamaRelevantChatDefaults(
            Section("muse-max", overrideAll: false, temperature: 0.2));
        var after = SettingsCoreEndpoints.LlamaRelevantChatDefaults(
            Section("muse-max", overrideAll: false, temperature: 0.9));

        before.DefaultModelId.Should().Be("muse-max");
        after.DefaultModelId.Should().Be("muse-max");
        before.OverrideAllChatModels.Should().Be(after.OverrideAllChatModels);
    }

    [TestMethod]
    public void OverrideFlagToggled_SameModel_ProjectionChanges_SoTheSweepRuleIsReapplied()
    {
        var before = SettingsCoreEndpoints.LlamaRelevantChatDefaults(Section("muse-max", overrideAll: false));
        var after = SettingsCoreEndpoints.LlamaRelevantChatDefaults(Section("muse-max", overrideAll: true));

        before.DefaultModelId.Should().Be("muse-max");
        after.DefaultModelId.Should().Be("muse-max");
        before.OverrideAllChatModels.Should().BeFalse();
        after.OverrideAllChatModels.Should().BeTrue();
    }

    [TestMethod]
    public void MissingSection_ProjectsNoDefaultAndOverrideOff()
    {
        var projection = SettingsCoreEndpoints.LlamaRelevantChatDefaults(null);
        projection.DefaultModelId.Should().BeNull();
        projection.OverrideAllChatModels.Should().BeFalse();
    }

    [TestMethod]
    public void BlankDefaultModelId_ProjectsNull_SoNothingIsActuated()
    {
        var projection = SettingsCoreEndpoints.LlamaRelevantChatDefaults(Section(" ", overrideAll: true));
        projection.DefaultModelId.Should().BeNull();
        projection.OverrideAllChatModels.Should().BeTrue();
    }
}
