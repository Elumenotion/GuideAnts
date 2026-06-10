using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class ChatRunnerUtilsTests
{
    [TestMethod]
    public void BuildRunResults_MapsConversationMessagesAndUsage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.Developer, "developer"),
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "assistant response"),
            new("tool-call-1", "lookup", [new ChatContent("tool output")])
        };

        var response = new ChatCompletionResponse(
            [new ChatChoice(new ChatMessage(ChatRole.Assistant, "assistant response"), "stop")],
            new ChatCompletionUsage
            {
                PromptTokens = 10,
                CompletionTokens = 4,
                TotalTokens = 14,
                PromptTokensDetails = new ChatPromptTokensDetails { CachedTokens = 3 }
            });

        var result = ChatRunnerUtils.BuildRunResults(messages, response);

        result.Should().NotBeNull();
        result!.LastMessage.Should().Be("tool output");
        result.Status.Should().Be("stop");
        result.ConversationMessages.Should().HaveCount(3);
        result.ConversationMessages[0].MessageType.Should().Be(ThreadConversationMessageType.User);
        result.ConversationMessages[0].Message.Should().Be("hello");
        result.ConversationMessages[1].MessageType.Should().Be(ThreadConversationMessageType.Assistant);
        result.ConversationMessages[1].Message.Should().Be("assistant response\n");
        result.ConversationMessages[2].MessageType.Should().Be(ThreadConversationMessageType.Tool);
        result.ConversationMessages[2].Message.Should().Be("tool output");
        result.Usage.Should().NotBeNull();
        result.Usage!.PromptTokens.Should().Be(10);
        result.Usage.CompletionTokens.Should().Be(4);
        result.Usage.CachedPromptTokens.Should().Be(3);
        result.Usage.TotalTokens.Should().Be(14);
    }

    [TestMethod]
    public void BuildRunResults_WhenChoiceAndUsageMissing_UsesUnknownAndZeroes()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var response = new ChatCompletionResponse([], usage: null);

        var result = ChatRunnerUtils.BuildRunResults(messages, response);

        result.Should().NotBeNull();
        result!.Status.Should().Be("unknown");
        result.Usage.Should().NotBeNull();
        result.Usage!.PromptTokens.Should().Be(0);
        result.Usage.CompletionTokens.Should().Be(0);
        result.Usage.CachedPromptTokens.Should().Be(0);
        result.Usage.TotalTokens.Should().Be(0);
    }

    [TestMethod]
    public void FilterJsonBySchema_RemovesUnknownFieldsRecursively()
    {
        using var contentDoc = JsonDocument.Parse("""
            {
              "name": "alpha",
              "details": { "level": 2, "note": "remove-me" },
              "items": [
                { "id": "a1", "extra": "x" },
                { "id": "b2", "extra": "y" }
              ],
              "ignoreRoot": "drop"
            }
            """);

        using var schemaDoc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "details": {
                  "type": "object",
                  "properties": {
                    "level": { "type": "number" }
                  }
                },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" }
                    }
                  }
                }
              }
            }
            """);

        var filtered = ChatRunnerUtils.FilterJsonBySchema(contentDoc.RootElement, schemaDoc.RootElement);

        filtered.GetProperty("name").GetString().Should().Be("alpha");
        filtered.TryGetProperty("ignoreRoot", out _).Should().BeFalse();
        filtered.GetProperty("details").TryGetProperty("note", out _).Should().BeFalse();
        filtered.GetProperty("details").GetProperty("level").GetDouble().Should().Be(2);
        filtered.GetProperty("items").GetArrayLength().Should().Be(2);
        filtered.GetProperty("items")[0].GetProperty("id").GetString().Should().Be("a1");
        filtered.GetProperty("items")[0].TryGetProperty("extra", out _).Should().BeFalse();
        filtered.GetProperty("items")[1].GetProperty("id").GetString().Should().Be("b2");
        filtered.GetProperty("items")[1].TryGetProperty("extra", out _).Should().BeFalse();
    }
}
