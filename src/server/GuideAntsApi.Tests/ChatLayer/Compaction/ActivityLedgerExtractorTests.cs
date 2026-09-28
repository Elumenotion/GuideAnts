using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class ActivityLedgerExtractorTests
{
    private static CompactionBlock ToolCall(string id, string name, string? argsJson) =>
        new(CompactionBlockKind.ToolCall, string.Empty, ToolCallId: id, ToolName: name, ToolArguments: argsJson);

    private static CompactionBlock ToolResult(string id, string name, bool? succeeded) =>
        new(CompactionBlockKind.ToolResult, "result", ToolCallId: id, ToolName: name, ToolSucceeded: succeeded);

    [TestMethod]
    public void Extract_NoToolCalls_ReturnsEmptyLedger()
    {
        ActivityLedgerExtractor.Extract([]).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_PairsCallWithItsResult()
    {
        var blocks = new List<CompactionBlock>
        {
            ToolCall("c1", "ReadFile", "{\"path\":\"a.txt\"}"),
            ToolResult("c1", "ReadFile", true)
        };

        var ledger = ActivityLedgerExtractor.Extract(blocks);

        ledger.Should().ContainSingle();
        ledger[0].ToolName.Should().Be("ReadFile");
        ledger[0].KeyArgument.Should().Be("a.txt");
        ledger[0].Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public void Extract_PicksThePriorityKeyWhenPresent()
    {
        var blocks = new List<CompactionBlock>
        {
            ToolCall("c1", "WebSearch", "{\"count\":5,\"query\":\"llama.cpp context window\"}")
        };

        ActivityLedgerExtractor.Extract(blocks)[0].KeyArgument.Should().Be("llama.cpp context window");
    }

    [TestMethod]
    public void Extract_FallsBackToFirstPropertyWhenNoPriorityKeyMatches()
    {
        var blocks = new List<CompactionBlock> { ToolCall("c1", "CustomTool", "{\"target\":\"thing\"}") };

        ActivityLedgerExtractor.Extract(blocks)[0].KeyArgument.Should().Be("thing");
    }

    [TestMethod]
    public void Extract_NoResultYet_SucceededIsNull()
    {
        var blocks = new List<CompactionBlock> { ToolCall("c1", "ReadFile", null) };

        ActivityLedgerExtractor.Extract(blocks)[0].Succeeded.Should().BeNull();
    }

    [TestMethod]
    public void Extract_MalformedArguments_FallsBackToTruncatedRawText()
    {
        var blocks = new List<CompactionBlock> { ToolCall("c1", "Weird", "not json") };

        ActivityLedgerExtractor.Extract(blocks)[0].KeyArgument.Should().Be("not json");
    }
}
