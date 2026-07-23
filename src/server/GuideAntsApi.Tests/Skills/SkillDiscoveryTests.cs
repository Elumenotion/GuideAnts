using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class SkillDiscoveryTests
{
    [TestMethod]
    public void BuildDiscoveryBlock_ContainsTier1Only()
    {
        var skills = new List<SkillDescriptor>
        {
            new()
            {
                Name = "pptx-author",
                Description = "Build decks",
                Locator = "skill://abc/pptx-author"
            }
        };

        var block = SkillDiscoveryBlockBuilder.BuildDiscoveryBlock(skills);

        block.Should().Contain("pptx-author");
        block.Should().Contain("Build decks");
        block.Should().Contain("skill://abc/pptx-author");
        block.Should().Contain("skills_read");
        block.Should().NotContain("SECRET_BODY_TEXT");
    }

    [TestMethod]
    public void FilterVisibleSkills_HidesWhenRequiredToolMissing()
    {
        var def = new AssistantDefinition
        {
            Skills =
            [
                new SkillDescriptor
                {
                    Name = "needs-web",
                    Description = "Needs web",
                    RequiresToolsets = ["web"]
                },
                new SkillDescriptor
                {
                    Name = "always",
                    Description = "Always visible"
                }
            ],
            Tools =
            [
                new ToolDefinition { Type = "code_interpreter" }
            ]
        };

        var visible = SkillVisibilityFilter.FilterVisibleSkills(def, "linux");
        visible.Select(s => s.Name).Should().Equal("always");
    }

    [TestMethod]
    public void FilterVisibleSkills_HidesFallbackWhenPrimaryToolPresent()
    {
        var def = new AssistantDefinition
        {
            Skills =
            [
                new SkillDescriptor
                {
                    Name = "fallback",
                    Description = "Fallback skill",
                    FallbackForToolsets = ["web"]
                }
            ],
            Tools =
            [
                new ToolDefinition
                {
                    Type = "function",
                    Function = new AssistantsApiToolFunctionOneOfType
                    {
                        AsObject = new FunctionDefinition { Name = "WebSearch" }
                    }
                }
            ]
        };

        SkillVisibilityFilter.FilterVisibleSkills(def, "linux").Should().BeEmpty();
    }
}
