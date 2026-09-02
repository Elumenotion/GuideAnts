using System.Threading.Channels;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Progress tokens may be dropped when the SSE consumer is behind. Terminal events
/// (complete / error / cancelled / pending_client_tool) must wait for capacity so the client
/// is told the run is over instead of hanging on an open stream.
/// </summary>
internal static class ConversationStreamEventWriter
{
    public static bool IsTerminal(string eventType) =>
        eventType is StreamingEventTypes.Error
            or StreamingEventTypes.Cancelled
            or StreamingEventTypes.PendingClientTool
            or StreamingEventTypes.Complete;

    public static void WriteTerminal(
        ChannelWriter<StreamingEvent> writer,
        StreamingEvent ev,
        TimeSpan wait)
    {
        if (writer.TryWrite(ev))
        {
            return;
        }

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

    public static async ValueTask WriteTerminalAsync(
        ChannelWriter<StreamingEvent> writer,
        StreamingEvent ev,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        if (writer.TryWrite(ev))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(wait);
        try
        {
            await writer.WriteAsync(ev, cts.Token).ConfigureAwait(false);
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
