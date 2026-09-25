using AntRunner.Chat.Compaction;
using AntRunner.ToolCalling;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class NoiseFilterTests
{
    [TestMethod]
    public void Filter_RemovesEmptyTextBlocks_ButKeepsToolBlocksWithEmptyText()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "   "),
            new(CompactionBlockKind.AssistantMessage, ""),
            new(CompactionBlockKind.SystemMessage, ""),
            new(CompactionBlockKind.ToolCall, "", ToolCallId: "c1", ToolName: "ReadFile"),
            new(CompactionBlockKind.ToolResult, "", ToolCallId: "c1", ToolName: "ReadFile")
        };

        var filtered = NoiseFilter.Filter(blocks);

        filtered.Should().HaveCount(2);
        filtered.Should().AllSatisfy(b => b.Kind.Should().BeOneOf(CompactionBlockKind.ToolCall, CompactionBlockKind.ToolResult));
    }

    [TestMethod]
    public void Filter_RemovesRuntimeOverrideSystemMessage()
    {
        var limitState = new ToolLimitState(5, 5, LimitEscalationPhase.SoftBlocked, ToolLimitHitKind.ToolCalls);
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.SystemMessage, limitState.BuildRuntimeOverrideSystemMessage())
        };

        NoiseFilter.Filter(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Filter_RemovesToolLimitReachedSystemNudge()
    {
        var limitState = new ToolLimitState(5, 5, LimitEscalationPhase.None);
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.SystemMessage, limitState.BuildSystemNudgeMessage(ToolLimitHitKind.ToolCalls))
        };

        NoiseFilter.Filter(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Filter_RemovesForceCompleteAssistantMessage()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.AssistantMessage,
                ToolLimitState.BuildForceCompleteAssistantMessage(ToolLimitHitKind.ToolCalls))
        };

        NoiseFilter.Filter(blocks).Should().BeEmpty();
    }

    [TestMethod]
    public void Filter_KeepsOrdinaryContent()
    {
        var blocks = new List<CompactionBlock>
        {
            new(CompactionBlockKind.UserMessage, "please read the file"),
            new(CompactionBlockKind.AssistantMessage, "sure, one moment")
        };

        NoiseFilter.Filter(blocks).Should().HaveCount(2);
    }
}
