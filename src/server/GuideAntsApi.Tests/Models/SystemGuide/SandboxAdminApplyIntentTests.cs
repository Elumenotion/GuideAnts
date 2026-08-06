using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Models.SystemGuide;

namespace GuideAntsApi.Tests.Models.SystemGuide;

[TestClass]
public sealed class SandboxAdminApplyIntentTests
{
    [TestMethod]
    public void ResolveTargets_global_returns_apt_only()
    {
        SandboxAdminApplyIntent.ResolveTargets(hasScope: false)
            .Should().Equal("apt");
    }

    [TestMethod]
    public void ResolveTargets_scoped_returns_pip_and_install_scripts()
    {
        SandboxAdminApplyIntent.ResolveTargets(hasScope: true)
            .Should().Equal("pip", "installScripts");
    }

    [TestMethod]
    public void ResolveForwardBody_without_body_synthesizes_global_apt_targets()
    {
        var body = SandboxAdminApplyIntent.ResolveForwardBody(rawBody: null, hasScope: false);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("targets").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("apt");
    }

    [TestMethod]
    public void ResolveForwardBody_without_body_synthesizes_scoped_targets()
    {
        var body = SandboxAdminApplyIntent.ResolveForwardBody(rawBody: string.Empty, hasScope: true);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("targets").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("pip", "installScripts");
    }

    [TestMethod]
    public void ResolveForwardBody_preserves_caller_supplied_json()
    {
        const string raw = """{"targets":["pip"]}""";

        SandboxAdminApplyIntent.ResolveForwardBody(raw, hasScope: true).Should().Be(raw);
    }

    [TestMethod]
    public void ResolveForwardBody_synthesized_targets_are_explicit_apply_intent_only()
    {
        foreach (var (hasScope, expected) in new (bool, string[])[]
                 {
                     (false, new[] { "apt" }),
                     (true, new[] { "pip", "installScripts" })
                 })
        {
            var body = SandboxAdminApplyIntent.ResolveForwardBody(rawBody: null, hasScope: hasScope);
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("targets").EnumerateArray()
                .Select(element => element.GetString())
                .Should().Equal(expected);
        }
    }
}
