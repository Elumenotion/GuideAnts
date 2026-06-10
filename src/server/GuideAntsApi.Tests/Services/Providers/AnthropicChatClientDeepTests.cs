using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Deterministic HTTP-handler coverage for <see cref="AntRunner.Chat.Anthropic.AnthropicChatClient"/>.
/// Each test drives the real client (via the factory) against a captured fake transport and asserts both
/// the outbound request shape (URL / headers / serialized body) and the mapped response.
/// </summary>
[TestClass]
public sealed class AnthropicChatClientDeepTests
{
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";

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
            DefaultMaxTokens = 4096
        };
        return new AnthropicChatClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);
    }

    private static string TextMessageJson(
        string text = "hello from claude",
        string stopReason = "end_turn",
        int inputTokens = 5,
        int outputTokens = 3) =>
        $$"""
        {
          "id": "msg_123",
          "type": "message",
          "role": "assistant",
          "model": "claude-haiku-4-5-20251001",
          "content": [ { "type": "text", "text": "{{text}}" } ],
          "stop_reason": "{{stopReason}}",
          "stop_sequence": null,
          "usage": { "input_tokens": {{inputTokens}}, "output_tokens": {{outputTokens}} }
        }
        """;

    [TestMethod]
    public async Task GetCompletionAsync_TextHappyPath_PostsMessagesAndMapsContentAndUsage()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "msg_abc",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "text", "text": "Hello " },
                { "type": "text", "text": "world" }
              ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 10,
                "output_tokens": 4,
                "cache_read_input_tokens": 3,
                "cache_creation_input_tokens": 2
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null));

        handler.LastRequestUri!.ToString().Should().Be(MessagesUrl);
        handler.LastRequestHeaders.TryGetValues("x-api-key", out var apiKey).Should().BeTrue();
        apiKey!.Single().Should().Be("ant-key");

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("model").GetString().Should().Be("claude-haiku-4-5-20251001");
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(4096);
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");

        response.FirstChoice!.Message.GetText().Should().Be("Hello world");
        response.FirstChoice.FinishReason.Should().Be("stop");
        // input_tokens(10) + cache_read(3) + cache_creation(2) = 15
        response.Usage!.PromptTokens.Should().Be(15);
        response.Usage.CompletionTokens.Should().Be(4);
        response.Usage.TotalTokens.Should().Be(19);
        response.Usage.PromptTokensDetails!.CachedTokens.Should().Be(3);
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithoutThinking_ProjectsTemperatureAndTopP()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null,
            samplingParameters: new Dictionary<string, double>
            {
                ["temperature"] = 0.3,
                ["top_p"] = 0.85
            }));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("temperature").GetDouble().Should().BeApproximately(0.3, 1e-9);
        body.RootElement.GetProperty("top_p").GetDouble().Should().BeApproximately(0.85, 1e-9);
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithUnprojectableSamplingKey_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null,
            samplingParameters: new Dictionary<string, double> { ["top_k"] = 40 }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*top_k*Anthropic*");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithToolsAndToolUseResponse_MapsToolCalls()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "msg_tool",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "text", "text": "Looking it up" },
                {
                  "type": "tool_use",
                  "id": "toolu_1",
                  "name": "lookup_weather",
                  "input": { "city": "Boston" }
                }
              ],
              "stop_reason": "tool_use",
              "stop_sequence": null,
              "usage": { "input_tokens": 7, "output_tokens": 9 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Weather?")],
            tools:
            [
                new ChatToolDefinition(new ChatFunctionDefinition(
                    "lookup_weather",
                    "Look up weather",
                    JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")))
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var tool = body.RootElement.GetProperty("tools")[0];
        tool.GetProperty("name").GetString().Should().Be("lookup_weather");
        tool.GetProperty("input_schema").GetProperty("properties").GetProperty("city").GetProperty("type").GetString()
            .Should().Be("string");
        tool.GetProperty("input_schema").GetProperty("required")[0].GetString().Should().Be("city");

        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        var toolCall = response.FirstChoice.Message.ToolCalls![0];
        toolCall.Id.Should().Be("toolu_1");
        toolCall.Function.Name.Should().Be("lookup_weather");
        toolCall.Function.Arguments.GetProperty("city").GetString().Should().Be("Boston");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithSystemAssistantToolMessages_SerializesEachRole()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var assistantToolCall = new ChatToolCall
        {
            Id = "toolu_42",
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "lookup_weather",
                Arguments = JsonSerializer.SerializeToElement(new { city = "Boston" })
            }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, "You are helpful."),
                new ChatMessage(ChatRole.User, "Weather?"),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("Let me check")], [assistantToolCall]),
                new ChatMessage("toolu_42", "lookup_weather", [new ChatContent("72F and sunny")])
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("system")[0].GetProperty("text").GetString().Should().Be("You are helpful.");

        var messages = body.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);
        messages[0].GetProperty("role").GetString().Should().Be("user");

        // assistant message carries a tool_use block
        var assistant = messages[1];
        assistant.GetProperty("role").GetString().Should().Be("assistant");
        var assistantBlocks = assistant.GetProperty("content");
        assistantBlocks.EnumerateArray()
            .Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
            .Should().BeTrue();

        // tool result is emitted as a user message with a tool_result block
        var toolResult = messages[2];
        toolResult.GetProperty("role").GetString().Should().Be("user");
        var resultBlock = toolResult.GetProperty("content")[0];
        resultBlock.GetProperty("type").GetString().Should().Be("tool_result");
        resultBlock.GetProperty("tool_use_id").GetString().Should().Be("toolu_42");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithBase64ImageContent_SerializesBase64Source()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        const string dataUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new("Describe this"),
                    new(new ChatImageUrl(dataUrl))
                })
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var blocks = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        var imageBlock = blocks.EnumerateArray().Single(b =>
            b.TryGetProperty("type", out var t) && t.GetString() == "image");
        var source = imageBlock.GetProperty("source");
        source.GetProperty("type").GetString().Should().Be("base64");
        source.GetProperty("media_type").GetString().Should().Be("image/png");
        source.GetProperty("data").GetString().Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithRemoteImageUrl_SerializesUrlSource()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new(new ChatImageUrl("https://example.test/cat.png"))
                })
            ],
            model: null));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var imageBlock = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        imageBlock.GetProperty("type").GetString().Should().Be("image");
        var source = imageBlock.GetProperty("source");
        source.GetProperty("type").GetString().Should().Be("url");
        source.GetProperty("url").GetString().Should().Be("https://example.test/cat.png");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonBase64DataUrl_ThrowsBeforeSend()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new(new ChatImageUrl("data:image/png,not-base64-data"))
                })
            ],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*base64*");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_UnsupportedImageMediaType_ThrowsBeforeSend()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new(new ChatImageUrl("data:image/bmp;base64,QUJD"))
                })
            ],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*media type*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolSchemaNotObject_ThrowsBeforeSend()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            tools:
            [
                new ChatToolDefinition(new ChatFunctionDefinition(
                    "bad_tool",
                    "bad",
                    JsonNode.Parse("""{"type":"array"}""")))
            ],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*object*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MissingModel_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient, new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = null,
            DefaultMaxTokens = 4096
        });

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Anthropic requires a model identifier.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_OnlySystemMessages_ThrowsRequiresAtLeastOneMessage()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.System, "You are helpful.")],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Anthropic requires at least one message.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolMessageWithoutToolCallId_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hi"),
                new ChatMessage(string.Empty, "fn", [new ChatContent("result")])
            ],
            model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tool message requires a tool_call_id.");
    }

    [TestMethod]
    [DataRow("max_tokens", "length")]
    [DataRow("stop_sequence", "stop")]
    [DataRow("end_turn", "stop")]
    [DataRow("tool_use", "tool_calls")]
    public async Task GetCompletionAsync_MapsStopReason(string stopReason, string expected)
    {
        // tool_use requires a tool_use content block to be a valid Anthropic response;
        // for the non-tool stop reasons a text block is sufficient.
        var contentJson = stopReason == "tool_use"
            ? """[ { "type": "tool_use", "id": "toolu_x", "name": "f", "input": {} } ]"""
            : """[ { "type": "text", "text": "done" } ]""";

        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            $$"""
            {
              "id": "msg_sr",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": {{contentJson}},
              "stop_reason": "{{stopReason}}",
              "stop_sequence": null,
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null));

        response.FirstChoice!.FinishReason.Should().Be(expected);
    }

    [TestMethod]
    [DataRow("minimal", 1100)]
    [DataRow("low", 2048)]
    [DataRow("medium", 3000)]
    [DataRow("high", 3500)]
    public async Task GetCompletionAsync_WithThinking_UsesConfiguredBudgetForEffort(string effort, int expectedBudget)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient, new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 8192,
            ThinkingBudgets = new AnthropicThinkingBudgets(
                Minimal: 1100, Low: 2048, Medium: 3000, High: 3500)
        });

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null,
            reasoningEffort: effort));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(expectedBudget);
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithThinkingBudgetBelowMinimum_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient, new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 8192,
            ThinkingBudgets = new AnthropicThinkingBudgets(Low: 512)
        });

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null,
            reasoningEffort: "low"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Thinking budget must be at least 1024 tokens.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithThinkingBudgetAboveMaxTokens_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(TextMessageJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient, new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 2000,
            ThinkingBudgets = new AnthropicThinkingBudgets(Medium: 4096)
        });

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null,
            reasoningEffort: "medium"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Thinking budget must be less than max tokens.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonSuccessStatus_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{ "type": "error", "error": { "type": "rate_limit_error", "message": "slow down" } }""",
            System.Net.HttpStatusCode.TooManyRequests));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Hi")],
            model: null));

        await act.Should().ThrowAsync<Exception>();
        handler.LastRequestUri!.ToString().Should().Be(MessagesUrl);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_TextEvents_AggregatesAndReportsUsage()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":6,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(
                messages: [new ChatMessage(ChatRole.User, "Hi")],
                model: null),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();

        deltas.Should().Equal("Hel", "lo");
        response.FirstChoice!.Message.GetText().Should().Be("Hello");
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage!.CompletionTokens.Should().Be(5);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ToolUseEvents_AccumulatesToolCallArguments()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_t","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":6,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_9","name":"lookup_weather","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"Boston\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":12}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(
                messages: [new ChatMessage(ChatRole.User, "Weather?")],
                model: null),
            _ => { });

        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        var toolCall = response.FirstChoice.Message.ToolCalls![0];
        toolCall.Id.Should().Be("toolu_9");
        toolCall.Function.Name.Should().Be("lookup_weather");
        toolCall.Function.Arguments.GetProperty("city").GetString().Should().Be("Boston");
    }
}
