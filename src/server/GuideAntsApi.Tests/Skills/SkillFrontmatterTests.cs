using System.Reflection;
using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class SkillFrontmatterTests
{
  private const string AgentskillsYaml = """
---
name: pptx-author
description: Build export-ready PowerPoint decks from an outline.
platforms: [windows, linux]
metadata:
  guideants:
    enabled: true
    display_order: 10
    requires_toolsets: [sandbox]
    requires_tools: [WebSearch]
---
# Body

Use this skill for decks.
""";

    private const string HermesYaml = """
---
name: hermes-skill
description: Hermes dialect skill.
metadata:
  hermes:
    enabled: true
    display_order: 5
    requires_toolsets: [web]
---
Body text.
""";

    private const string ClaudeCodeYaml = """
---
name: claude-skill
description: Claude Code dialect skill.
allowed-tools:
  - Bash
argument-hint: "[topic]"
---
Slash command body.
""";

    [TestMethod]
    public void Parse_AgentskillsDialect_ExtractsGuideantsMetadata()
    {
        var fm = SkillFrontmatter.Parse(AgentskillsYaml);

        fm.Name.Should().Be("pptx-author");
        fm.Description.Should().Contain("PowerPoint");
        fm.Enabled.Should().BeTrue();
        fm.DisplayOrder.Should().Be(10);
        fm.RequiresToolsets.Should().Contain("sandbox");
        fm.RequiresTools.Should().Contain("WebSearch");
        fm.Platforms.Should().Contain("windows");
    }

    [TestMethod]
    public void Parse_HermesDialect_ToleratesMetadataHermes()
    {
        var fm = SkillFrontmatter.Parse(HermesYaml);

        fm.Name.Should().Be("hermes-skill");
        fm.DisplayOrder.Should().Be(5);
        fm.RequiresToolsets.Should().Contain("web");
    }

    [TestMethod]
    public void Parse_ClaudeCodeDialect_ToleratesAllowedTools()
    {
        var fm = SkillFrontmatter.Parse(ClaudeCodeYaml);

        fm.Name.Should().Be("claude-skill");
        fm.RequiresTools.Should().Contain("Bash");
    }

    [TestMethod]
    public void Parse_MissingName_ThrowsExplicitError()
    {
        var act = () => SkillFrontmatter.Parse("""
---
description: no name here
---
body
""");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing required field 'name'*");
    }

    [TestMethod]
    public void Parse_MissingDescription_ThrowsExplicitError()
    {
        var act = () => SkillFrontmatter.Parse("""
---
name: orphan
---
body
""");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing required field 'description'*");
    }

    [TestMethod]
    public void ExtractBody_ReturnsMarkdownAfterFrontmatter()
    {
        SkillFrontmatter.ExtractBody(AgentskillsYaml).Should().Contain("# Body");
        SkillFrontmatter.ExtractBody(AgentskillsYaml).Should().NotContain("pptx-author");
    }
}
