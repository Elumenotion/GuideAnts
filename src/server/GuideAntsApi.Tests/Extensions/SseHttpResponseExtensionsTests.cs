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
}
