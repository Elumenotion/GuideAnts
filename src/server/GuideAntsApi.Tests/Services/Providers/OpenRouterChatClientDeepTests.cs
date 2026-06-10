using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenRouter;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class OpenRouterChatClientDeepTests
{
    [TestMethod]
    public async Task GetCompletionAsync_BuildsEndpoint_AndOptionalHeaders()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig
        {
            ApiKey = "or-key",
            BaseUrl = "https://openrouter.ai/api/v1/",
            HttpReferer = "https://example.test",
            AppTitle = "GuideAnts"
        }, "openai/gpt-4o-mini");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
        handler.LastRequestHeaders.TryGetValues("Authorization", out var auth).Should().BeTrue();
        auth!.Single().Should().Be("Bearer or-key");
        handler.LastRequestHeaders.TryGetValues("HTTP-Referer", out var referer).Should().BeTrue();
        referer!.Single().Should().Be("https://example.test");
        handler.LastRequestHeaders.TryGetValues("X-Title", out var title).Should().BeTrue();
        title!.Single().Should().Be("GuideAnts");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenApiKeyMissing_BeforeSend()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("OpenRouter:ApiKey is required.");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenModelMissing()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, null);

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("OpenRouter chat requires a model identifier.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_UsesDefaultModel_WhenRequestModelNull()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "default/model");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("model").GetString().Should().Be("default/model");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsDescriptiveError_OnNonSuccess()
    {
        var handler = new OrCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream boom", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("OpenRouter chat request failed (502): upstream boom");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenBodyNull()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse("null"));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("OpenRouter chat response was empty.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenChoicesMissing()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse("""{"usage":{"total_tokens":1}}"""));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("OpenRouter response did not contain choices.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_SerializesSamplingAndReasoning()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "high",
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["temperature"] = 0.5,
                ["top_p"] = 0.9,
                ["top_k"] = 30
            }));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.5);
        json.RootElement.GetProperty("top_p").GetDouble().Should().Be(0.9);
        json.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
        json.RootElement.GetProperty("top_k").GetDouble().Should().Be(30);
    }

    [TestMethod]
    public async Task GetCompletionAsync_SingleTextMessage_SerializesContentAsString()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "plain")], model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("plain");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MultiPartMessage_SerializesContentAsArray()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new("describe"),
                    new(new ChatImageUrl("https://img/x.png"))
                })
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.Array);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[1].GetProperty("type").GetString().Should().Be("image_url");
        content[1].GetProperty("image_url").GetProperty("url").GetString().Should().Be("https://img/x.png");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolMessage_SerializesToolCallIdAndName()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage("call_42", "lookup", [new ChatContent("result text")])
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var toolMsg = json.RootElement.GetProperty("messages")[1];
        toolMsg.GetProperty("role").GetString().Should().Be("tool");
        toolMsg.GetProperty("tool_call_id").GetString().Should().Be("call_42");
        toolMsg.GetProperty("name").GetString().Should().Be("lookup");
        toolMsg.GetProperty("content").GetString().Should().Be("result text");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantToolCalls_SerializeArgumentsFromObjectAndString()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var objArgsCall = new ChatToolCall
        {
            Id = "c1",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "objfn", Arguments = JsonSerializer.SerializeToElement(new { a = 1 }) }
        };
        var strArgsCall = new ChatToolCall
        {
            Id = "c2",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "strfn", Arguments = JsonSerializer.SerializeToElement("{\"b\":2}") }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent> { new("calling") }, new List<ChatToolCall> { objArgsCall, strArgsCall })
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var toolCalls = json.RootElement.GetProperty("messages")[1].GetProperty("tool_calls");
        toolCalls[0].GetProperty("function").GetProperty("arguments").GetString().Should().Be("{\"a\":1}");
        toolCalls[1].GetProperty("function").GetProperty("arguments").GetString().Should().Be("{\"b\":2}");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsForUnsupportedContentPart()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, new List<ChatContent> { new() })],
            model: null);

        var act = () => client.GetCompletionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unsupported OpenRouter content item.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsToolCallsAndUsageDetails()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "ok",
                    "tool_calls": [
                      { "id": "call_1", "type": "function", "function": { "name": "f", "arguments": "{\"x\":1}" } }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 5, "completion_tokens": 2, "total_tokens": 7, "prompt_tokens_details": { "cached_tokens": 3 } }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().Be("ok");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls![0].Function.Arguments.GetProperty("x").GetInt32().Should().Be(1);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.PromptTokensDetails!.CachedTokens.Should().Be(3);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ExtractsArrayContent()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(
            """
            { "choices": [ { "message": { "content": [ { "type": "text", "text": "part-a" }, { "type": "text", "text": "part-b" } ] }, "finish_reason": "stop" } ] }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().Be("part-apart-b");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ExtractsObjectContent()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(
            """
            { "choices": [ { "message": { "content": { "text": "obj-text" } }, "finish_reason": "stop" } ] }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().Be("obj-text");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullContent_YieldsEmptyMessage()
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(
            """
            { "choices": [ { "message": { "content": null }, "finish_reason": "stop" } ] }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().BeEmpty();
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_NormalizesFinishReasonVariants()
    {
        await AssertFinish("length", "length");
        await AssertFinish("max_tokens", "length");
        await AssertFinish("tool_use", "tool_calls");
        await AssertFinish("weird", "stop");
    }

    private static async Task AssertFinish(string upstream, string expected)
    {
        var handler = new OrCapturingHandler(_ => JsonResponse(
            $$"""{ "choices": [ { "message": { "content": "x" }, "finish_reason": "{{upstream}}" } ] }"""));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.FinishReason.Should().Be(expected);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_AggregatesTextToolCallsAndUsage()
    {
        var handler = new OrCapturingHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"role":"assistant","content":"Hel"}}]}

            data: {"choices":[{"delta":{"content":"lo","tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"math","arguments":"{\"n\":"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"7}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: [DONE]

            """));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k", BaseUrl = "https://openrouter.ai/api/v1" }, "m");

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        deltas.Should().Equal("Hel", "lo");
        response.FirstChoice!.Message.GetText().Should().Be("Hello");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls![0].Id.Should().Be("call_a");
        response.FirstChoice.Message.ToolCalls[0].Function.Name.Should().Be("math");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("n").GetInt32().Should().Be(7);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.TotalTokens.Should().Be(5);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_FinishReasonOnlyChunk_WithoutDelta()
    {
        var handler = new OrCapturingHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"content":"done"}}]}

            data: {"choices":[{"finish_reason":"stop"}]}

            data: [DONE]

            """));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("done");
        response.FirstChoice.FinishReason.Should().Be("stop");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ThrowsDescriptiveError_OnNonSuccess()
    {
        var handler = new OrCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        var act = () => client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null), _ => { });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("OpenRouter chat stream failed (500): server error");
    }

    private static string TextResponse(string text) =>
        $$"""{ "choices": [ { "message": { "content": "{{text}}" }, "finish_reason": "stop" } ] }""";

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SseResponse(string sse)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class OrCapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HeaderSnapshot LastRequestHeaders { get; private set; } = new(new HttpRequestMessage().Headers);
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestHeaders = new HeaderSnapshot(request.Headers);
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class HeaderSnapshot
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _headers;

        public HeaderSnapshot(HttpRequestHeaders headers)
        {
            _headers = headers.ToDictionary(
                h => h.Key,
                h => (IReadOnlyList<string>)h.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetValues(string name, out IReadOnlyList<string>? values) =>
            _headers.TryGetValue(name, out values);
    }
}
