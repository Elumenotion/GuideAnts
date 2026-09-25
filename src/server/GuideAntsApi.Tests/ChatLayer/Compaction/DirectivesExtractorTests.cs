using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class DirectivesExtractorTests
{
    [TestMethod]
    public void Extract_NoUserMessages_ReturnsEmpty()
    {
        DirectivesExtractor.Extract([]).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_MatchesAlwaysNeverPreferDont()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Always run tests before committing"),
            new(CompactionBlockKind.UserMessage, "Never commit directly to main"),
            new(CompactionBlockKind.UserMessage, "I prefer tabs over spaces"),
            new(CompactionBlockKind.UserMessage, "Don't use --force"),
            new(CompactionBlockKind.UserMessage, "What time is it?")
        };

        var directives = DirectivesExtractor.Extract(blocks);

        directives.Should().HaveCount(4);
        directives.Select(d => d.Text).Should().NotContain(t => t.Contains("What time"));
    }

    [TestMethod]
    public void Extract_IgnoresNonUserBlocks()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.AssistantMessage, "I will always confirm before deleting files")
        };

        DirectivesExtractor.Extract(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_IsCaseInsensitive()
    {
        var blocks = new List<CompactionBlock> { new(CompactionBlockKind.UserMessage, "ALWAYS ask first") };

        DirectivesExtractor.Extract(blocks).Should().ContainSingle();
    }
}
