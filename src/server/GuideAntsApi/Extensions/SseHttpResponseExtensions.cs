using System.Text;
using GuideAntsApi.Models.Conversations;
using Microsoft.AspNetCore.Http.Features;

namespace GuideAntsApi;

public static class SseHttpResponseExtensions
{
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);

    public static async Task WriteSseEventAsync(
        this HttpResponse response,
        string eventType,
        string data,
        CancellationToken cancellationToken = default)
    {
        var eventData = $"event: {eventType}\ndata: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(eventData);
        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    public static async Task WriteSseCommentAsync(
        this HttpResponse response,
        string comment,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes($": {comment}\n\n");
        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes conversation SSE events and emits comment keepalives while the
    /// engine is silent so the client can tell a live wait from a dead socket.
    /// </summary>
    /// <remarks>
    /// Compiler-generated async iterators throw <see cref="NotSupportedException"/> from
    /// <c>DisposeAsync</c> when an outstanding <c>MoveNextAsync</c> has not completed.
    /// Keepalive uses <see cref="Task.WhenAny"/>, so a pending move-next is normal; cleanup
    /// must cancel and await that task before disposing the enumerator.
    /// </remarks>
    public static async Task WriteSseStreamWithKeepAliveAsync(
        this HttpResponse response,
        IAsyncEnumerable<StreamingEvent> events,
        CancellationToken cancellationToken,
        TimeSpan? keepAliveInterval = null)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var interval = keepAliveInterval ?? DefaultKeepAliveInterval;
        using var enumeratorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = events.GetAsyncEnumerator(enumeratorCts.Token);
        Task<bool>? moveNextTask = null;
        try
        {
            moveNextTask = enumerator.MoveNextAsync().AsTask();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(interval, delayCts.Token);
                var completed = await Task.WhenAny(moveNextTask, delayTask).ConfigureAwait(false);
                if (completed != moveNextTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await response.WriteSseCommentAsync("keepalive", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                delayCts.Cancel();
                var hasNext = await moveNextTask.ConfigureAwait(false);
                moveNextTask = null;
                if (!hasNext)
                {
                    break;
                }

                var ev = enumerator.Current;
                await response.WriteSseEventAsync(ev.EventType, ev.Payload, cancellationToken).ConfigureAwait(false);
                moveNextTask = enumerator.MoveNextAsync().AsTask();
            }
        }
        finally
        {
            // Cancel first so a blocked MoveNextAsync can complete, then await it.
            // Disposing while MoveNextAsync is in flight throws NotSupportedException and
            // aborts long-running conversation streams (observed as mid-turn SSE halts).
            try
            {
                enumeratorCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (moveNextTask is not null)
            {
                try
                {
                    await moveNextTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                    // Tear-down only: preserve the original exception from the try block.
                }
            }

            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
