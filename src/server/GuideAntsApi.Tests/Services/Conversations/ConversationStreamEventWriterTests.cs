using System.Threading.Channels;
using FluentAssertions;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationStreamEventWriterTests
{
    [TestMethod]
    public void IsTerminal_RecognizesErrorCancelledAndPendingClientTool()
    {
        ConversationStreamEventWriter.IsTerminal(StreamingEventTypes.Error).Should().BeTrue();
        ConversationStreamEventWriter.IsTerminal(StreamingEventTypes.Cancelled).Should().BeTrue();
        ConversationStreamEventWriter.IsTerminal(StreamingEventTypes.PendingClientTool).Should().BeTrue();
        ConversationStreamEventWriter.IsTerminal(StreamingEventTypes.AssistantMessage).Should().BeFalse();
        ConversationStreamEventWriter.IsTerminal(StreamingEventTypes.Token).Should().BeFalse();
    }

    [TestMethod]
    public async Task WriteTerminal_WaitsForCapacityInsteadOfDropping()
    {
        var channel = Channel.CreateBounded<StreamingEvent>(1);
        channel.Writer.TryWrite(new StreamingEvent(StreamingEventTypes.Token, "{}")).Should().BeTrue();

        var writeTask = Task.Run(() => ConversationStreamEventWriter.WriteTerminal(
            channel.Writer,
            new StreamingEvent(StreamingEventTypes.Error, "{\"code\":\"local_llm_timeout\"}"),
            TimeSpan.FromSeconds(2)));

        await Task.Delay(50);
        writeTask.IsCompleted.Should().BeFalse();

        var first = await channel.Reader.ReadAsync();
        first.EventType.Should().Be(StreamingEventTypes.Token);

        await writeTask;
        var terminal = await channel.Reader.ReadAsync();
        terminal.EventType.Should().Be(StreamingEventTypes.Error);
        terminal.Payload.Should().Contain("local_llm_timeout");
    }
}
