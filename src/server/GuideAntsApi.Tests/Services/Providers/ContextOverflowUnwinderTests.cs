using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class ContextOverflowUnwinderTests
{
    [TestMethod]
    public void PrefersOldestToolPair_OverLargerNewerPair_AndLargerThinking()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("first"),
            AssistantCall("old-call"),
            ToolResult("old-call", "tiny"),
            User("second"),
            AssistantCall("new-call"),
            ToolResult("new-call", new string('x', 8000)),
            AssistantThinking("keep this", new string('t', 9000)),
            User("current question")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out var result).Should().BeTrue();
        result.Phase.Should().Be(ContextOverflowUnwindPhase.ToolPair);
        result.RemovedMessages.Should().HaveCount(2);
        result.RemovedMessages[0].ToolCalls.Should().ContainSingle(c => c.Id == "old-call");
        result.RemovedMessages[1].ToolCallId.Should().Be("old-call");

        messages.Select(ToolIdOrText).Should().Equal(
            "system:guide",
            "user:first",
            "user:second",
            "call:new-call",
            "tool:new-call",
            "assistant:keep this",
            "user:current question");
    }

    [TestMethod]
    public void ToolPairEviction_RemovesAssistantCallAndAllMatchingResults()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("go"),
            AssistantCalls("a", "b"),
            ToolResult("a", "result-a"),
            ToolResult("b", "result-b"),
            User("again")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out var result).Should().BeTrue();
        result.Phase.Should().Be(ContextOverflowUnwindPhase.ToolPair);
        result.RemovedMessages.Should().HaveCount(3);
        messages.Should().HaveCount(3);
        messages.Last().GetText().Should().Be("again");
    }

    [TestMethod]
    public void AfterToolPairs_StripsLargestThinking_KeepsVisibleText()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("q1"),
            AssistantThinking("short visible", "abc"),
            User("q2"),
            AssistantThinking("keep visible", new string('z', 400)),
            User("current")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out var result).Should().BeTrue();
        result.Phase.Should().Be(ContextOverflowUnwindPhase.Thinking);
        result.ThinkingStrippedFrom.Should().NotBeNull();
        result.ThinkingStrippedFrom!.GetText().Should().Be("keep visible");
        result.ThinkingStrippedFrom.ThinkingBlocks.Should().BeNull();

        var kept = messages.Single(m => m.GetText() == "keep visible");
        kept.ThinkingBlocks.Should().BeNull();
        messages.Single(m => m.GetText() == "short visible").ThinkingBlocks.Should().NotBeNull();
    }

    [TestMethod]
    public void AfterThinking_EvictsOldestNonSystem_ProtectsLatestUserAndSystem()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("old user"),
            AssistantText("old assistant answer"),
            User("current question")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out var result).Should().BeTrue();
        result.Phase.Should().Be(ContextOverflowUnwindPhase.OldestNonSystem);
        result.RemovedMessages.Should().ContainSingle(m => m.GetText() == "old user");

        messages.Select(m => m.GetText()).Should().Equal(
            "guide",
            "old assistant answer",
            "current question");
    }

    [TestMethod]
    public void DoesNotEvictWhenOnlySystemAndCurrentUserRemain()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("current")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out var result).Should().BeFalse();
        result.DidUnwind.Should().BeFalse();
        messages.Should().HaveCount(2);
    }

    [TestMethod]
    public void EvictsOrphanToolResult_BeforeThinking()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("q"),
            ToolResult("orphan", "orphan-output"),
            AssistantThinking("text", new string('t', 5000)),
            User("current")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out var result).Should().BeTrue();
        result.Phase.Should().Be(ContextOverflowUnwindPhase.ToolPair);
        result.RemovedMessages.Should().ContainSingle(m => m.ToolCallId == "orphan");
        messages.Should().Contain(m => m.GetText() == "text");
    }

    [TestMethod]
    public void ThinkingTie_StripsOldest()
    {
        var messages = new List<ChatMessage>
        {
            System("guide"),
            User("q1"),
            AssistantThinking("first", "12345"),
            User("q2"),
            AssistantThinking("second", "12345"),
            User("current")
        };

        ContextOverflowUnwinder.TryUnwind(messages, out _).Should().BeTrue();
        messages.Single(m => m.GetText() == "first").ThinkingBlocks.Should().BeNull();
        messages.Single(m => m.GetText() == "second").ThinkingBlocks.Should().NotBeNull();
    }

    [TestMethod]
    public void IsEvictionNotice_RecognizesNewAndLegacyMarkers()
    {
        ContextOverflowUnwinder.IsEvictionNotice(ContextOverflowUnwinder.ToolPairEvictionNotice).Should().BeTrue();
        ContextOverflowUnwinder.IsEvictionNotice("[Message aborted due to size restrictions]").Should().BeTrue();
        ContextOverflowUnwinder.IsEvictionNotice("normal tool output").Should().BeFalse();
    }

    private static ChatMessage System(string text) => new(ChatRole.System, text);

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage AssistantThinking(string text, string thinking) =>
        new(ChatRole.Assistant, [new ChatContent(text)], null, [ChatThinkingBlock.ForThinking(thinking, "")]);

    private static ChatMessage AssistantCall(string id) =>
        new(ChatRole.Assistant, Array.Empty<ChatContent>(), [Call(id)], null);

    private static ChatMessage AssistantCalls(params string[] ids) =>
        new(ChatRole.Assistant, Array.Empty<ChatContent>(), ids.Select(Call).ToList(), null);

    private static ChatMessage ToolResult(string id, string content) =>
        new(id, "run_bash", [new ChatContent(content)]);

    private static ChatToolCall Call(string id)
    {
        using var doc = JsonDocument.Parse("""{"script":"x"}""");
        return new ChatToolCall
        {
            Id = id,
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "run_bash",
                Arguments = doc.RootElement.Clone()
            }
        };
    }

    private static string ToolIdOrText(ChatMessage message)
    {
        if (message.Role == ChatRole.System)
        {
            return $"system:{message.GetText()}";
        }

        if (message.Role == ChatRole.User)
        {
            return $"user:{message.GetText()}";
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            return $"call:{message.ToolCalls[0].Id}";
        }

        if (message.Role == ChatRole.Tool)
        {
            return $"tool:{message.ToolCallId}";
        }

        return $"assistant:{message.GetText()}";
    }
}
