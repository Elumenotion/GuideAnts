using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Compaction;
using AntRunner.ToolCalling;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class CompactionEngineTests
{
    [TestMethod]
    public void Compact_EmptyInputs_ReturnsAWellFormedSummaryWithNoExceptions()
    {
        var result = CompactionEngine.Compact([], []);

        result.SummaryText.Should().Contain("(none recorded)");
        result.SummarizedMessageCount.Should().Be(0);
        result.SummarizedTurnCount.Should().Be(0);
    }

    [TestMethod]
    public void Compact_TypicalConversation_ProducesAllSections()
    {
        var call = new ChatToolCall
        {
            Id = "c1",
            Function = new ChatToolCallFunction
            {
                Name = "RunScript",
                Arguments = JsonDocument.Parse("{\"command\":\"pytest\"}").RootElement.Clone()
            }
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Build a CSV export feature"),
            new(ChatRole.Assistant, [], [call]),
            new("c1", "RunScript", [new ChatContent("{\"standardOutput\":\"\",\"standardError\":\"failed\",\"exitCode\":1}")]),
            new(ChatRole.User, "Always write a test for every fix"),
            new(ChatRole.Assistant, "Understood, I'll add tests going forward.")
        };

        var turnFacts = new List<TurnFacts> { new(1, ["out/report.csv"], []) };

        var result = CompactionEngine.Compact(messages, turnFacts);

        result.SummarizedMessageCount.Should().Be(5);
        result.SummarizedTurnCount.Should().Be(1);
        result.SummaryText.Should().Contain("Build a CSV export feature");
        result.SummaryText.Should().Contain("out/report.csv");
        result.SummaryText.Should().Contain("RunScript");
        result.SummaryText.Should().Contain("Always write a test for every fix");
    }

    [TestMethod]
    public void Compact_DropsWhitespaceOnlyLeadingUserMessage_SoTheRealMessageBecomesTheGoal()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "   "),
            new(ChatRole.User, "Build a CSV export feature")
        };

        var result = CompactionEngine.Compact(messages, []);

        result.SummaryText.Should().Contain("## Goal\nBuild a CSV export feature");
        result.SummarizedMessageCount.Should().Be(2); // counts raw input messages, not filtered blocks
    }

    [TestMethod]
    public void Compact_MultipleTurnFactsSharingATurnIndex_CountsDistinctTurns()
    {
        var turnFacts = new List<TurnFacts>
        {
            new(1, ["a.txt"], []),
            new(1, [], ["b.txt"])
        };

        var result = CompactionEngine.Compact([], turnFacts);

        result.SummarizedTurnCount.Should().Be(1);
    }

    [TestMethod]
    public void Compact_SameInputTwice_IsDeterministic()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Build a CSV export feature"),
            new(ChatRole.Assistant, "On it.")
        };
        var turnFacts = new List<TurnFacts> { new(1, ["out.csv"], []) };

        var first = CompactionEngine.Compact(messages, turnFacts);
        var second = CompactionEngine.Compact(messages, turnFacts);

        first.SummaryText.Should().Be(second.SummaryText);
        first.SummarizedMessageCount.Should().Be(second.SummarizedMessageCount);
    }

    [TestMethod]
    public void Compact_NullInputs_DoesNotThrow()
    {
        var act = () => CompactionEngine.Compact(null!, null!);

        act.Should().NotThrow();
    }
}
