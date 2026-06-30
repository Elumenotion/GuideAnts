using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Models.Conversations;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Endpoints.PublishedWire;

[TestClass]
public sealed class WireStreamAdapterTests
{
    [TestMethod]
    public async Task WriteOpenAiChatCompletionsSseAsync_Emits_incremental_token_chunks()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"Hel\"}"),
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"lo\"}"),
            new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":1,\"completion_tokens\":2}"));

        var state = new WireStreamAdapter.OpenAiChatStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteOpenAiChatCompletionsSseAsync(
                           events,
                           "chatcmpl_test",
                           "guide",
                           created: 1,
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var contentChunks = chunks
            .Where(chunk => chunk.Contains("\"content\"", StringComparison.Ordinal) && chunk.Contains("chat.completion.chunk", StringComparison.Ordinal))
            .ToList();
        contentChunks.Count.Should().BeGreaterThanOrEqualTo(2);
        string.Join(string.Empty, ExtractContentDeltas(chunks)).Should().Be("Hello");
        chunks[^1].Should().Contain("data: [DONE]");
        state.PromptTokens.Should().Be(1);
        state.CompletionTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task CollectWireConversationResultAsync_Folds_token_stream_to_concatenated_text()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"Hel\"}"),
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"lo\"}"),
            new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":1,\"completion_tokens\":2}"));

        await using var db = CreateDbContext();
        var result = await WireConversationExecutor.CollectWireConversationResultAsync(
            events,
            db,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.Text.Should().Be("Hello");
        result.PromptTokens.Should().Be(1);
        result.CompletionTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task WriteOpenAiChatCompletionsSseAsync_Maps_error_event_to_error_object()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Error, "{\"message\":\"provider failed\",\"type\":\"provider_error\"}"));

        var state = new WireStreamAdapter.OpenAiChatStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteOpenAiChatCompletionsSseAsync(
                           events,
                           "chatcmpl_test",
                           "guide",
                           created: 1,
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle();
        chunks[0].Should().Contain("\"error\"");
        chunks[0].Should().Contain("provider failed");
        state.ErrorPayload.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task WriteOpenAiResponsesSseAsync_Emits_incremental_token_deltas()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"Hel\"}"),
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"lo\"}"),
            new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":1,\"completion_tokens\":2}"));

        var state = new WireStreamAdapter.OpenAiResponsesStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteOpenAiResponsesSseAsync(
                           events,
                           "resp_test",
                           "guide",
                           created: 1,
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var deltaChunks = chunks
            .Where(chunk => chunk.Contains("response.output_text.delta", StringComparison.Ordinal))
            .ToList();
        deltaChunks.Count.Should().BeGreaterThanOrEqualTo(2);
        string.Join(string.Empty, ExtractResponsesTextDeltas(chunks)).Should().Be("Hello");
        chunks.Should().Contain(chunk => chunk.Contains("event: response.completed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WriteAnthropicMessagesSseAsync_Emits_incremental_text_deltas()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"Hel\"}"),
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"lo\"}"),
            new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":1,\"completion_tokens\":2}"));

        var state = new WireStreamAdapter.AnthropicMessagesStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteAnthropicMessagesSseAsync(
                           events,
                           "msg_test",
                           "guide",
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var deltaChunks = chunks
            .Where(chunk => chunk.Contains("content_block_delta", StringComparison.Ordinal) &&
                            chunk.Contains("text_delta", StringComparison.Ordinal))
            .ToList();
        deltaChunks.Count.Should().BeGreaterThanOrEqualTo(2);
        string.Join(string.Empty, ExtractAnthropicTextDeltas(chunks)).Should().Be("Hello");
        chunks.Should().Contain(chunk => chunk.Contains("event: message_stop", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WriteOpenAiResponsesSseAsync_Does_not_surface_tool_calls_for_text_only_stream()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"answer\"}"),
            new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":1,\"completion_tokens\":1}"));

        var state = new WireStreamAdapter.OpenAiResponsesStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteOpenAiResponsesSseAsync(
                           events,
                           "resp_test",
                           "guide",
                           created: 1,
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var body = string.Join(string.Empty, chunks);
        body.Should().NotContain("function_call");
        body.Should().NotContain("tool_calls");
        state.PendingClientTool.Should().BeFalse();
    }

    [TestMethod]
    public async Task WriteAnthropicMessagesSseAsync_Does_not_surface_tool_use_for_text_only_stream()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"answer\"}"),
            new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":1,\"completion_tokens\":1}"));

        var state = new WireStreamAdapter.AnthropicMessagesStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteAnthropicMessagesSseAsync(
                           events,
                           "msg_test",
                           "guide",
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var body = string.Join(string.Empty, chunks);
        body.Should().NotContain("\"type\":\"tool_use\"");
        state.PendingClientTool.Should().BeFalse();
    }

    [TestMethod]
    public async Task WriteOpenAiResponsesSseAsync_Maps_error_event()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Error, "{\"message\":\"provider failed\",\"type\":\"api_error\"}"));

        var state = new WireStreamAdapter.OpenAiResponsesStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteOpenAiResponsesSseAsync(
                           events,
                           "resp_test",
                           "guide",
                           created: 1,
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle();
        chunks[0].Should().Contain("event: error");
        chunks[0].Should().Contain("provider failed");
    }

    [TestMethod]
    public async Task WriteAnthropicMessagesSseAsync_Maps_error_event()
    {
        var events = StreamEvents(
            new StreamingEvent(StreamingEventTypes.Error, "{\"message\":\"provider failed\",\"type\":\"api_error\"}"));

        var state = new WireStreamAdapter.AnthropicMessagesStreamState();
        var chunks = new List<string>();
        await foreach (var chunk in WireStreamAdapter.WriteAnthropicMessagesSseAsync(
                           events,
                           "msg_test",
                           "guide",
                           publicApiOrigin: null,
                           state,
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        chunks.Should().ContainSingle();
        chunks[0].Should().Contain("event: error");
        chunks[0].Should().Contain("provider failed");
    }

    private static IEnumerable<string> ExtractResponsesTextDeltas(IEnumerable<string> sseChunks)
    {
        foreach (var chunk in sseChunks)
        {
            if (!chunk.Contains("response.output_text.delta", StringComparison.Ordinal))
            {
                continue;
            }

            var dataIndex = chunk.IndexOf("data: ", StringComparison.Ordinal);
            if (dataIndex < 0)
            {
                continue;
            }

            var json = chunk[(dataIndex + "data: ".Length)..].Trim();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
            {
                yield return delta.GetString() ?? string.Empty;
            }
        }
    }

    private static IEnumerable<string> ExtractAnthropicTextDeltas(IEnumerable<string> sseChunks)
    {
        foreach (var chunk in sseChunks)
        {
            if (!chunk.Contains("text_delta", StringComparison.Ordinal))
            {
                continue;
            }

            var dataIndex = chunk.IndexOf("data: ", StringComparison.Ordinal);
            if (dataIndex < 0)
            {
                continue;
            }

            var json = chunk[(dataIndex + "data: ".Length)..].Trim();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                yield return text.GetString() ?? string.Empty;
            }
        }
    }

    private static IEnumerable<string> ExtractContentDeltas(IEnumerable<string> sseChunks)
    {
        foreach (var chunk in sseChunks)
        {
            if (!chunk.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var json = chunk["data: ".Length..].Trim();
            if (json == "[DONE]")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                yield return content.GetString() ?? string.Empty;
            }
        }
    }

    private static async IAsyncEnumerable<StreamingEvent> StreamEvents(params StreamingEvent[] events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"wire-stream-adapter-tests-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}
