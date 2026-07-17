using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Tests.Options;

[TestClass]
public sealed class ScriptExecutionOptionsTests
{
    [TestMethod]
    public void SettingsSectionRegistry_IncludesScriptExecutionTimeout()
    {
        var registry = new SettingsSectionRegistry();

        registry.TryGet(ScriptExecutionOptions.SectionName, out var definition).Should().BeTrue();
        var timeoutProperty = definition!.Properties.Single(property => property.Name == "TimeoutSeconds");
        timeoutProperty.CanonicalKey.Should().Be("ScriptExecution:TimeoutSeconds");
        timeoutProperty.DefaultValue.Should().Be(600);
    }

    [TestMethod]
    public void DefaultTimeoutSeconds_IsTenMinutes()
    {
        new ScriptExecutionOptions().TimeoutSeconds.Should().Be(600);
    }

    [TestMethod]
    public void HttpClientTimeout_UsesConfiguredSeconds()
    {
        var options = new ScriptExecutionOptions { TimeoutSeconds = 900 };

        options.HttpClientTimeout.Should().Be(TimeSpan.FromSeconds(900));
    }

    [TestMethod]
    public void HttpClientTimeout_ClampsNonPositiveValuesToOneSecond()
    {
        var options = new ScriptExecutionOptions { TimeoutSeconds = 0 };

        options.HttpClientTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }
}
