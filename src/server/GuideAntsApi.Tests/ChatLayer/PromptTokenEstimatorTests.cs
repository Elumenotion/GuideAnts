using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class PromptTokenEstimatorTests
{
    [TestMethod]
    public void CountChars_SumsTextAcrossMessages()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello"),      // 5
            new ChatMessage(ChatRole.Assistant, "world!!") // 7
        };

        PromptTokenEstimator.CountChars(messages).Should().Be(12);
    }

    [TestMethod]
    public void CountChars_IncludesToolCallNameAndArguments()
    {
        var call = new ChatToolCall
        {
            Id = "c1",
            Function = new ChatToolCallFunction
            {
                Name = "search",
                Arguments = JsonSerializer.SerializeToElement("{\"q\":\"x\"}")
            }
        };
        var message = new ChatMessage(ChatRole.Assistant, new List<ChatContent>(), new List<ChatToolCall> { call });

        // "search" (6) + "{\"q\":\"x\"}" (9)
        PromptTokenEstimator.CountChars([message]).Should().Be(15);
    }

    [TestMethod]
    public void CountChars_IncludesObjectToolCallArguments()
    {
        var call = new ChatToolCall
        {
            Id = "c1",
            Function = new ChatToolCallFunction
            {
                Name = "search",
                Arguments = JsonSerializer.SerializeToElement(new { q = "x" })
            }
        };
        var message = new ChatMessage(ChatRole.Assistant, new List<ChatContent>(), new List<ChatToolCall> { call });

        // "search" (6) + {"q":"x"} (9)
        PromptTokenEstimator.CountChars([message]).Should().Be(15);
    }

    [TestMethod]
    public void CountChars_IgnoresImageContent()
    {
        var message = new ChatMessage(ChatRole.User,
            new List<ChatContent> { new(new ChatImageUrl("data:image/png;base64,AAAA")) });

        PromptTokenEstimator.CountChars([message]).Should().Be(0);
    }

    [TestMethod]
    public void EstimateTokens_UsesDefaultRatio()
    {
        PromptTokenEstimator.EstimateTokens(400).Should().Be(100);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-2.0)]
    [DataRow(double.NaN)]
    public void EstimateTokens_InvalidRatio_FallsBackToDefault(double ratio)
    {
        PromptTokenEstimator.EstimateTokens(400, ratio).Should().Be(100);
    }

    [TestMethod]
    public void EstimateTokens_RoundsUp()
    {
        PromptTokenEstimator.EstimateTokens(401).Should().Be(101);
    }

    [TestMethod]
    public void CharsPerToken_NoObservations_ReturnsDefault()
    {
        PromptTokenEstimator.CharsPerToken([]).Should().Be(PromptTokenEstimator.DefaultCharsPerToken);
    }

    [TestMethod]
    public void CharsPerToken_PoolsObservations()
    {
        // 300 chars / 100 tokens and 100 chars / 50 tokens => 400 / 150
        var ratio = PromptTokenEstimator.CharsPerToken([(100, 300), (50, 100)]);

        ratio.Should().BeApproximately(400.0 / 150.0, 1e-9);
    }

    [TestMethod]
    public void CharsPerToken_IgnoresNonPositiveObservations()
    {
        PromptTokenEstimator.CharsPerToken([(0, 500), (100, 0)])
            .Should().Be(PromptTokenEstimator.DefaultCharsPerToken);
    }

    [TestMethod]
    public void CharsPerToken_ClampsToSaneRange()
    {
        PromptTokenEstimator.CharsPerToken([(1, 1000)]).Should().Be(8.0);
        PromptTokenEstimator.CharsPerToken([(1000, 1)]).Should().Be(1.5);
    }
}
