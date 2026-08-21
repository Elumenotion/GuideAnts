using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Tests.Extensions;

[TestClass]
public sealed class SseHttpResponseExtensionsTests
{
    [TestMethod]
    public async Task WriteSseStreamWithKeepAlive_WritesCommentWhileEnumeratorIsIdle()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        async IAsyncEnumerable<StreamingEvent> SlowEvents()
        {
            await Task.Delay(40);
            yield return new StreamingEvent("token", "{\"d\":1}");
        }

        await http.Response.WriteSseStreamWithKeepAliveAsync(
            SlowEvents(),
            CancellationToken.None,
            keepAliveInterval: TimeSpan.FromMilliseconds(10));

        http.Response.Body.Position = 0;
        var text = new StreamReader(http.Response.Body, Encoding.UTF8).ReadToEnd();
        text.Should().Contain(": keepalive");
        text.Should().Contain("event: token");
        text.Should().Contain("data: {\"d\":1}");
        http.Response.ContentType.Should().Be("text/event-stream");
    }

    [TestMethod]
    public async Task WriteSseStreamWithKeepAlive_WritesEventsWithoutKeepaliveWhenReady()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        async IAsyncEnumerable<StreamingEvent> ImmediateEvents()
        {
            yield return new StreamingEvent("complete", "{}");
            await Task.CompletedTask;
        }

        await http.Response.WriteSseStreamWithKeepAliveAsync(
            ImmediateEvents(),
            CancellationToken.None,
            keepAliveInterval: TimeSpan.FromSeconds(30));

        http.Response.Body.Position = 0;
        var text = new StreamReader(http.Response.Body, Encoding.UTF8).ReadToEnd();
        text.Should().Contain("event: complete");
        text.Should().NotContain(": keepalive");
    }

    [TestMethod]
    public async Task WriteSseStreamWithKeepAlive_CancelWhileMoveNextPending_DoesNotThrowNotSupported()
    {
        // Reproduces production halt: keepalive leaves MoveNextAsync outstanding; cancel/dispose
        // of the async iterator must not throw NotSupportedException and tear down the turn.
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<StreamingEvent> BlockedEvents(
            [EnumeratorCancellation] CancellationToken ct)
        {
            enteredDelay.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }

        var writeTask = http.Response.WriteSseStreamWithKeepAliveAsync(
            BlockedEvents(CancellationToken.None),
            cts.Token,
            keepAliveInterval: TimeSpan.FromMilliseconds(5));

        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);
        cts.Cancel();

        var act = async () => await writeTask;
        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().NotBeOfType<NotSupportedException>();
    }

    [TestMethod]
    public async Task WriteSseStreamWithKeepAlive_WriteFailureWhileMoveNextPending_DoesNotThrowNotSupported()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new ThrowAfterKeepaliveStream();
        using var cts = new CancellationTokenSource();
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<StreamingEvent> BlockedEvents(
            [EnumeratorCancellation] CancellationToken ct)
        {
            enteredDelay.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }

        var writeTask = http.Response.WriteSseStreamWithKeepAliveAsync(
            BlockedEvents(CancellationToken.None),
            cts.Token,
            keepAliveInterval: TimeSpan.FromMilliseconds(5));

        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var act = async () => await writeTask;
        var thrown = await act.Should().ThrowAsync<IOException>();
        thrown.Which.Should().NotBeOfType<NotSupportedException>();
        thrown.Which.Message.Should().Contain("simulated write failure");
    }

    private sealed class ThrowAfterKeepaliveStream : MemoryStream
    {
        private int _writeCount;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeCount++;
            // First write is the keepalive comment; fail on a subsequent flush/write path
            // after MoveNext is already pending (same window as production).
            if (_writeCount > 1)
            {
                throw new IOException("simulated write failure");
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
