using System.Threading.Channels;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Progress tokens may be dropped when the SSE consumer is behind. Terminal events
/// (error / cancelled / pending_client_tool) must wait for capacity so the client
/// is told the run is over instead of hanging on an open stream.
/// </summary>
internal static class ConversationStreamEventWriter
{
    public static bool IsTerminal(string eventType) =>
        eventType is StreamingEventTypes.Error
            or StreamingEventTypes.Cancelled
            or StreamingEventTypes.PendingClientTool;

    public static void WriteTerminal(
        ChannelWriter<StreamingEvent> writer,
        StreamingEvent ev,
        TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            writer.WriteAsync(ev, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            writer.TryWrite(ev);
        }
        catch (ChannelClosedException)
        {
        }
    }
}
