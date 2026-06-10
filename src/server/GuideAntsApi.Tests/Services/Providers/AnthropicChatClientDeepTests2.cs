using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second wave of deterministic coverage for <see cref="AnthropicChatClient"/> covering the
/// thinking/redacted blocks, tool-input assembly, image media types, request-validation throws,
/// stop-reason mapping and streaming thinking/tool branches not exercised by the first deep file.
/// </summary>
[TestClass]
public sealed class AnthropicChatClientDeepTests2
{
    private static IChatCompletionClient CreateClient(
        CapturingHandler handler,
        HttpClient httpClient,
        AnthropicConfig? config = null)
    {
        config ??= new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 8192,
            ThinkingBudgets = new AnthropicThinkingBudgets(Low: 2048)
        };
        return new AnthropicChatClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);
    }

    private static string TextMessageJson(string text = "ok") =>
        $$"""
        {
          "id": "msg_1", "type": "message", "role": "assistant",
          "model": "claude-haiku-4-5-20251001",
          "content": [ { "type": "text", "text": "{{text}}" } ],
          "stop_reason": "end_turn", "stop_sequence": null,
          "usage": { "input_tokens": 1, "output_tokens": 1 }
        }
        """;

    private static ChatCompletionRequest UserRequest(string text = "Hi") =>
        new(messages: [new ChatMessage(ChatRole.User, text)], model: null);

    // ---- response mapping (FromMessage) ----

    [TestMethod]
    public async Task GetCompletionAsync_ThinkingBlock_MapsThinkingBlocks()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "msg_t", "type": "message", "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "thinking", "thinking": "let me think", "signature": "sig-1" },
                { "type": "text", "text": "answer" }
              ],
              "stop_reason": "end_turn", "stop_sequence": null,
              "usage": { "input_tokens": 2, "output_tokens": 3 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.FirstChoice!.Message.GetText().Should().Be("answer");
        response.FirstChoice.Message.ThinkingBlocks.Should().ContainSingle();
        response.FirstChoice.Message.ThinkingBlocks![0].Thinking.Should().Be("let me think");
        response.FirstChoice.Message.ThinkingBlocks[0].Signature.Should().Be("sig-1");
    }

    [TestMethod]
    public async Task GetCompletionAsync_RedactedThinkingBlock_MapsRedacted()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "msg_r", "type": "message", "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "redacted_thinking", "data": "encrypted-data" },
                { "type": "text", "text": "answer" }
              ],
              "stop_reason": "end_turn", "stop_sequence": null,
              "usage": { "input_tokens": 2, "output_tokens": 3 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.FirstChoice!.Message.ThinkingBlocks.Should().ContainSingle();
        response.FirstChoice.Message.ThinkingBlocks![0].IsRedactedThinking.Should().BeTrue();
        response.FirstChoice.Message.ThinkingBlocks[0].Data.Should().Be("encrypted-data");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ServerToolUseBlock_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "msg_s", "type": "message", "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "server_tool_use", "id": "srv_1", "name": "web_search", "input": {} }
              ],
              "stop_reason": "end_turn", "stop_sequence": null,
              "usage": { "input_tokens": 2, "output_tokens": 3 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Server tool blocks*");
    }

    [TestMethod]
    [DataRow("pause_turn", "stop")]
    [DataRow("refusal", "stop")]
    public async Task GetCompletionAsync_MapsAdditionalStopReasons(string stopReason, string expected)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            $$"""
            {
              "id": "msg_sr", "type": "message", "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [ { "type": "text", "text": "done" } ],
              "stop_reason": "{{stopReason}}", "stop_sequence": null,
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.FirstChoice!.FinishReason.Should().Be(expected);
    }

    [TestMethod]
    public async Task GetCompletionAsync_MissingStopReason_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "msg_n", "type": "message", "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [ { "type": "text", "text": "done" } ],
              "stop_reason": null, "stop_sequence": null,
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stop reason*");
    }

    // ---- request mapping (ToMessageCreateParams) ----

    [TestMethod]
    public async Task GetCompletionAsync_SystemMessageWithImage_SerializesImageTextBlock()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, new List<ChatContent>
                {
                    new("system text"),
                    new(new ChatImageUrl("https://example.test/logo.png"))
                }),
                new ChatMessage(ChatRole.User, "Hi")
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var system = body.RootElement.GetProperty("system");
        system.EnumerateArray().Select(b => b.GetProperty("text").GetString())
            .Should().Contain("system text").And.Contain("https://example.test/logo.png");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantThinkingBlocks_SerializedWhenThinkingEnabled()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hi"),
                new ChatMessage(
                    ChatRole.Assistant,
                    new List<ChatContent> { new("prior answer") },
                    null,
                    new List<ChatThinkingBlock>
                    {
                        ChatThinkingBlock.ForThinking("earlier thought", "sig-x"),
                        ChatThinkingBlock.ForRedacted("redacted-blob")
                    }),
                new ChatMessage(ChatRole.User, "continue")
            ],
            model: null,
            reasoningEffort: "low"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var assistant = body.RootElement.GetProperty("messages")
            .EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        var blockTypes = assistant.GetProperty("content").EnumerateArray()
            .Select(b => b.GetProperty("type").GetString()).ToList();
        blockTypes.Should().Contain("thinking");
        blockTypes.Should().Contain("redacted_thinking");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThinkingBlockMissingSignature_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hi"),
                new ChatMessage(
                    ChatRole.Assistant,
                    new List<ChatContent> { new("x") },
                    null,
                    new List<ChatThinkingBlock> { ChatThinkingBlock.ForThinking("thought", "") }),
                new ChatMessage(ChatRole.User, "continue")
            ],
            model: null,
            reasoningEffort: "low"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Thinking block requires*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolResultWithImage_SerializesImageResultBlock()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hi"),
                new ChatMessage("toolu_1", "render", new List<ChatContent>
                {
                    new("here is the chart"),
                    new(new ChatImageUrl("https://example.test/chart.png"))
                })
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var toolResult = body.RootElement.GetProperty("messages")
            .EnumerateArray().Last().GetProperty("content")[0];
        toolResult.GetProperty("type").GetString().Should().Be("tool_result");
        var inner = toolResult.GetProperty("content");
        inner.EnumerateArray().Any(b => b.GetProperty("type").GetString() == "image").Should().BeTrue();
    }

    [TestMethod]
    [DataRow("image/jpeg")]
    [DataRow("image/gif")]
    [DataRow("image/webp")]
    public async Task GetCompletionAsync_Base64ImageMediaTypes_AreMapped(string mediaType)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new(new ChatImageUrl($"data:{mediaType};base64,QUJD"))
                })
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var image = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        image.GetProperty("source").GetProperty("media_type").GetString().Should().Be(mediaType);
    }

    [TestMethod]
    public async Task GetCompletionAsync_DataUrlMissingData_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new(new ChatImageUrl("data:image/png;base64,"))
                })
            ],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*data URL*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolCallMissingId_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var toolCall = new ChatToolCall
        {
            Id = "",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "f", Arguments = JsonSerializer.SerializeToElement(new { }) }
        };

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hi"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent> { new("x") }, [toolCall])
            ],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Tool call id*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantToolCall_WithStringJsonArguments_BuildsToolInput()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var toolCall = new ChatToolCall
        {
            Id = "toolu_2",
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "lookup",
                // Arguments stored as a JSON *string* rather than an object.
                Arguments = JsonSerializer.SerializeToElement("{\"city\":\"Paris\"}")
            }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hi"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent> { new("x") }, [toolCall])
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var toolUse = body.RootElement.GetProperty("messages")
            .EnumerateArray().Last().GetProperty("content")
            .EnumerateArray().Single(b => b.GetProperty("type").GetString() == "tool_use");
        toolUse.GetProperty("input").GetProperty("city").GetString().Should().Be("Paris");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonFunctionToolDefinition_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            tools: [new ChatToolDefinition(null!)],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*function tools*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolParametersNull_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            tools: [new ChatToolDefinition(new ChatFunctionDefinition("f", "d", null))],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*parameters are required*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolParametersNotObjectSchema_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            tools: [new ChatToolDefinition(new ChatFunctionDefinition("f", "d", JsonNode.Parse("[]")))],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*JSON object schema*");
    }

    // ---- streaming accumulator ----

    [TestMethod]
    public async Task StreamCompletionAsync_ThinkingAndSignatureDeltas_ProduceThinkingBlock()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":6,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"seed ","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"more thoughts"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"final-sig"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"answer"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":7}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var emitted = new List<string>();
        var response = await client.StreamCompletionAsync(
            UserRequest(),
            chunk =>
            {
                var d = chunk.FirstChoice?.Delta.Content;
                if (!string.IsNullOrEmpty(d)) emitted.Add(d!);
            });

        response.FirstChoice!.Message.GetText().Should().Be("answer");
        response.FirstChoice.Message.ThinkingBlocks.Should().ContainSingle();
        response.FirstChoice.Message.ThinkingBlocks![0].Thinking.Should().Be("seed more thoughts");
        response.FirstChoice.Message.ThinkingBlocks[0].Signature.Should().Be("final-sig");
        // Thinking content is streamed as deltas, then a paragraph separator on block stop.
        emitted.Should().Contain("seed ").And.Contain("more thoughts");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_RedactedThinkingStart_ProducesRedactedBlock()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":6,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"enc-data"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest(), _ => { });

        response.FirstChoice!.Message.ThinkingBlocks.Should().ContainSingle();
        response.FirstChoice.Message.ThinkingBlocks![0].IsRedactedThinking.Should().BeTrue();
        response.FirstChoice.Message.ThinkingBlocks[0].Data.Should().Be("enc-data");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ToolUseStartInputNoDeltas_UsesStartInput()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":6,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_seed","name":"lookup","input":{"city":"Rome"}}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":3}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest(), _ => { });

        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        var toolCall = response.FirstChoice.Message.ToolCalls!.Single();
        toolCall.Id.Should().Be("toolu_seed");
        toolCall.Function.Arguments.GetProperty("city").GetString().Should().Be("Rome");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_InputJsonDeltaWithoutToolState_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":6,"output_tokens":1}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":5,"delta":{"type":"input_json_delta","partial_json":"{}"}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.StreamCompletionAsync(UserRequest(), _ => { });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*without tool state*");
    }
}
