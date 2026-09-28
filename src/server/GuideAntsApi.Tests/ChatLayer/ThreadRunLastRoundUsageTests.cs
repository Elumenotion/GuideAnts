using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class ThreadRunLastRoundUsageTests
{
    private static ChatCompletionUsage Usage(int prompt, int completion) =>
        new() { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion };

    [TestMethod]
    public void FirstRound_RecordsLastRoundValues()
    {
        var result = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), roundPromptChars: 4_000);

        result.LastRoundPromptTokens.Should().Be(1_000);
        result.LastRoundPromptChars.Should().Be(4_000);
    }

    [TestMethod]
    public void LaterRound_OverwritesLastRound_ButStillSumsTotals()
    {
        var first = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), 4_000);
        var second = ThreadRun.MergeRoundUsage(first, Usage(1_200, 30), 4_900);

        second.LastRoundPromptTokens.Should().Be(1_200);
        second.LastRoundPromptChars.Should().Be(4_900);
        second.PromptTokens.Should().Be(2_200, "accumulation is cumulative spend and must not change");
        second.CompletionTokens.Should().Be(80);
    }

    [TestMethod]
    public void RoundWithoutPromptTokens_KeepsPreviousLastRoundPair()
    {
        var first = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), 4_000);
        var second = ThreadRun.MergeRoundUsage(first, Usage(0, 30), 5_000);

        second.LastRoundPromptTokens.Should().Be(1_000);
        second.LastRoundPromptChars.Should().Be(4_000);
    }

    [TestMethod]
    public void FirstRoundWithoutPromptTokens_LeavesLastRoundNull()
    {
        var result = ThreadRun.MergeRoundUsage(null, Usage(0, 30), 5_000);

        result.LastRoundPromptTokens.Should().BeNull();
        result.LastRoundPromptChars.Should().BeNull();
    }

    [TestMethod]
    public void RoundWithZeroChars_KeepsTokensButNoCharPair()
    {
        var result = ThreadRun.MergeRoundUsage(null, Usage(1_000, 50), roundPromptChars: 0);

        result.LastRoundPromptTokens.Should().Be(1_000);
        result.LastRoundPromptChars.Should().BeNull();
    }

    [TestMethod]
    public void CopyLastRoundUsage_NoPair_LeavesUsageNull()
    {
        var output = new ChatRunOutput();
        ThreadRun.CopyLastRoundUsage(new UsageResponse { PromptTokens = 5 }, output);
        output.Usage.Should().BeNull();
    }

    [TestMethod]
    public void CopyLastRoundUsage_WithPair_CopiesPairOnly()
    {
        var output = new ChatRunOutput { Usage = new UsageResponse { PromptTokens = 7, CompletionTokens = 3 } };
        ThreadRun.CopyLastRoundUsage(
            new UsageResponse { PromptTokens = 99, LastRoundPromptTokens = 10, LastRoundPromptChars = 40 }, output);
        output.Usage!.LastRoundPromptTokens.Should().Be(10);
        output.Usage.LastRoundPromptChars.Should().Be(40);
        output.Usage.PromptTokens.Should().Be(7);
        output.Usage.CompletionTokens.Should().Be(3);
    }
}
