using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class SkillLocatorTests
{
    [TestMethod]
    public void FormatPublished_UsesGuideNameAndSkillName()
    {
        SkillLocator.FormatPublished("Creative Guide", "pptx-author")
            .Should().Be("skill://Creative Guide/pptx-author");
    }

    [TestMethod]
    public void TryParse_InternalLocator_ParsesAssistantIdAndName()
    {
        var assistantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        SkillLocator.TryParse($"skill://{assistantId}/demo", out var parts).Should().BeTrue();
        parts.IsPublished.Should().BeFalse();
        parts.AssistantId.Should().Be(assistantId);
        parts.SkillName.Should().Be("demo");
        parts.ReferenceRelativePath.Should().BeNull();
    }

    [TestMethod]
    public void TryParse_PublishedLocator_ParsesGuideAndName()
    {
        SkillLocator.TryParse("skill://Creative Guide/pptx-author", out var parts).Should().BeTrue();
        parts.IsPublished.Should().BeTrue();
        parts.GuideName.Should().Be("Creative Guide");
        parts.SkillName.Should().Be("pptx-author");
        parts.ReferenceRelativePath.Should().BeNull();
    }

    [TestMethod]
    public void TryParse_PublishedReferenceLocator_ParsesReferencePath()
    {
        SkillLocator.TryParse("skill://Creative Guide/demo/references/ref.md", out var parts).Should().BeTrue();
        parts.IsPublished.Should().BeTrue();
        parts.GuideName.Should().Be("Creative Guide");
        parts.SkillName.Should().Be("demo");
        parts.ReferenceRelativePath.Should().Be("ref.md");
    }

    [TestMethod]
    public void TryParse_RejectsTraversalInPublishedReference()
    {
        SkillLocator.TryParse("skill://Guide/demo/references/../secret.md", out _).Should().BeFalse();
    }
}
