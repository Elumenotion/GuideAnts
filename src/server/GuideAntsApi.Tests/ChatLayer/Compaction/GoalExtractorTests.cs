using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class GoalExtractorTests
{
    [TestMethod]
    public void Extract_NoUserMessages_ReturnsNullGoalAndNoScopeChanges()
    {
        var result = GoalExtractor.Extract([]);

        result.InitialGoal.Should().BeNull();
        result.ScopeChanges.Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_FirstUserMessage_BecomesTheGoal()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature"),
            new(CompactionBlockKind.AssistantMessage, "Sure, starting now")
        };

        GoalExtractor.Extract(blocks).InitialGoal.Should().Be("Build a CSV export feature");
    }

    [TestMethod]
    public void Extract_LaterUserMessageWithScopeMarker_IsRecordedAsAScopeChange()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature"),
            new(CompactionBlockKind.UserMessage, "Actually, let's do JSON export instead")
        };

        var result = GoalExtractor.Extract(blocks);

        result.ScopeChanges.Should().ContainSingle()
            .Which.Should().Be("Actually, let's do JSON export instead");
    }

    [TestMethod]
    public void Extract_LaterUserMessageWithoutScopeMarker_IsNotRecorded()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature"),
            new(CompactionBlockKind.UserMessage, "Also add unit tests please")
        };

        GoalExtractor.Extract(blocks).ScopeChanges.Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_IgnoresNonUserBlocks()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.SystemMessage, "system prompt"),
            new(CompactionBlockKind.UserMessage, "Build a CSV export feature")
        };

        GoalExtractor.Extract(blocks).InitialGoal.Should().Be("Build a CSV export feature");
    }

    [TestMethod]
    public void Extract_LongInitialGoal_IsTruncated()
    {
        var longText = new string('a', 600);
        var blocks = new List<CompactionBlock> { new(CompactionBlockKind.UserMessage, longText) };

        var goal = GoalExtractor.Extract(blocks).InitialGoal!;

        goal.Length.Should().Be(501); // 500 chars + ellipsis
        goal.Should().EndWith("…");
    }

    [TestMethod]
    public void Truncate_SurrogatePairAtTheCutBoundary_DoesNotSplitThePair()
    {
        // "😀" is encoded in UTF-16 as a surrogate pair (high + low). Position it so the cut
        // at index 500 would otherwise land between the two surrogate chars.
        var text = new string('a', 499) + "😀" + new string('b', 100);

        var act = () => CompactionText.Truncate(text, 500);

        var result = act.Should().NotThrow().Which;
        result.Should().Be(new string('a', 499) + "…");
        result.Should().NotContain("\uD83D");
        result.Should().NotContain("\uDE00");
    }
}
