using System.Reflection;
using AntRunner.Chat;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class SkillTraceSourceTests
{
    [TestMethod]
    public void ResolveToolTraceSource_TagsSkillsTools()
    {
        var method = typeof(ThreadRun).GetMethod(
            "ResolveToolTraceSource",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(null, ["skills_list"]).Should().Be("skills");
        method.Invoke(null, ["skills_read"]).Should().Be("skills");
        method.Invoke(null, ["WebSearch"]).Should().Be("guide");
    }

    [TestMethod]
    public void DiscoveryBlock_UsesTier1FieldsOnly()
    {
        var body = new string('X', 6000);
        var block = SkillDiscoveryBlockBuilder.BuildDiscoveryBlock(
        [
            new SkillDescriptor
            {
                Name = "large",
                Description = "Large skill",
                Locator = "skill://abc/large"
            }
        ]);

        block.Should().Contain("large");
        block.Should().NotContain(body);
    }
}
