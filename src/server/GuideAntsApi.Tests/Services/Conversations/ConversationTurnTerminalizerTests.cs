using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationTurnTerminalizerTests
{
    [TestMethod]
    public void MapTerminationCode_RecognizesLlamaInferenceTimeout()
    {
        var ex = new ChatConversationException(
            new LlamaInferenceTimeoutException("qwen", 30),
            chatRunOutput: null);

        ConversationTurnTerminalizer.MapTerminationCode(ex).Should().Be("local_llm_timeout");
    }

    [TestMethod]
    public void MapTerminalStatus_UsesPartialOutputStatusWhenPresent()
    {
        var output = new ChatRunOutput { Status = "timed_out" };
        ConversationTurnTerminalizer.MapTerminalStatus(output, null).Should().Be("timed_out");
    }
}
