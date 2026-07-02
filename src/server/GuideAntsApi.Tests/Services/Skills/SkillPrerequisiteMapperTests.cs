using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.Services.Guides.Skills;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class SkillPrerequisiteMapperTests
{
    [TestMethod]
    public void Map_SandboxToolset_AddsCodeInterpreterWithExplicitReason()
    {
        var frontmatter = SkillFrontmatter.Parse("""
---
name: demo
description: Demo
metadata:
  guideants:
    requires_toolsets: [sandbox]
---
body
""");

        var result = SkillPrerequisiteMapper.Map(frontmatter);

        result.NeedsCodeInterpreter.Should().BeTrue();
        result.Summary.Should().ContainSingle(item =>
            item.MappedCapability == "code_interpreter"
            && item.Reason.Contains("sandbox"));
    }

    [TestMethod]
    public void Map_WebToolset_AddsWebSearchAndReadWeb()
    {
        var frontmatter = SkillFrontmatter.Parse("""
---
name: demo
description: Demo
metadata:
  guideants:
    requires_toolsets: [web]
---
body
""");

        var result = SkillPrerequisiteMapper.Map(frontmatter);

        result.ToolIds.Should().HaveCount(2);
        result.Summary.Should().Contain(item => item.MappedCapability == "WebSearch");
        result.Summary.Should().Contain(item => item.MappedCapability == "ReadWeb");
    }

    [TestMethod]
    public void Map_UnknownToolset_DoesNotGuess()
    {
        var frontmatter = SkillFrontmatter.Parse("""
---
name: demo
description: Demo
metadata:
  guideants:
    requires_toolsets: [unknown-set]
---
body
""");

        var result = SkillPrerequisiteMapper.Map(frontmatter);

        result.ToolIds.Should().BeEmpty();
        result.NeedsCodeInterpreter.Should().BeFalse();
        result.Summary.Should().ContainSingle(item => item.MappedCapability == "(unmapped)");
    }
}
