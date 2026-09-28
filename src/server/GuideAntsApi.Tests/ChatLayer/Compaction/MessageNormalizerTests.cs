using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class MessageNormalizerTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void Normalize_UserMessage_ProducesUserMessageBlock()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "hello there") };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(CompactionBlockKind.UserMessage);
        blocks[0].Text.Should().Be("hello there");
    }

    [TestMethod]
    public void Normalize_SystemAndDeveloperMessages_ProduceSystemMessageBlocks()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.Developer, "developer note")
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().HaveCount(2);
        blocks.Should().OnlyContain(b => b.Kind == CompactionBlockKind.SystemMessage);
    }

    [TestMethod]
    public void Normalize_AssistantTextOnly_ProducesAssistantMessageBlock()
    {
        var messages = new[] { new ChatMessage(ChatRole.Assistant, "here is my answer") };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(CompactionBlockKind.AssistantMessage);
    }

    [TestMethod]
    public void Normalize_AssistantWithToolCalls_ProducesToolCallBlocksAndOmitsEmptyText()
    {
        var call = new ChatToolCall
        {
            Id = "call-1",
            Function = new ChatToolCallFunction { Name = "ReadFile", Arguments = Args("{\"path\":\"a.txt\"}") }
        };
        var messages = new[] { new ChatMessage(ChatRole.Assistant, [], [call]) };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks.Should().ContainSingle("the assistant message had no text, only a tool call");
        blocks[0].Kind.Should().Be(CompactionBlockKind.ToolCall);
        blocks[0].ToolCallId.Should().Be("call-1");
        blocks[0].ToolName.Should().Be("ReadFile");
        blocks[0].ToolArguments.Should().Be("{\"path\":\"a.txt\"}");
    }

    [TestMethod]
    public void Normalize_ToolResult_CapturesSuccessFromExitCode()
    {
        var messages = new[]
        {
            new ChatMessage("call-1", "ReadFile",
                [new ChatContent("{\"standardOutput\":\"hi\",\"standardError\":\"\",\"exitCode\":0}")])
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks[0].Kind.Should().Be(CompactionBlockKind.ToolResult);
        blocks[0].ToolSucceeded.Should().BeTrue();
    }

    [TestMethod]
    public void Normalize_ToolResult_NonZeroExitCode_IsFailure()
    {
        var messages = new[]
        {
            new ChatMessage("call-1", "RunScript",
                [new ChatContent("{\"standardOutput\":\"\",\"standardError\":\"boom\",\"exitCode\":1}")])
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks[0].ToolSucceeded.Should().BeFalse();
    }

    [TestMethod]
    public void Normalize_ToolResult_NonScriptExecutionShape_SucceededIsNull()
    {
        var messages = new[]
        {
            new ChatMessage("call-1", "SearchProject", [new ChatContent("[{\"score\":0.9}]")])
        };

        var blocks = MessageNormalizer.Normalize(messages);

        blocks[0].ToolSucceeded.Should().BeNull();
    }

    [TestMethod]
    public void Normalize_EmptyMessageList_ReturnsEmptyList()
    {
        MessageNormalizer.Normalize(Array.Empty<ChatMessage>()).Should().BeEmpty();
    }
}
