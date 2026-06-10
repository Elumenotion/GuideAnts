using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.HuggingFace;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class HuggingFaceChatClientDeepTests
{
    [TestMethod]
    public async Task GetCompletionAsync_BuildsEndpointAndAuthorization()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig
        {
            Token = "hf-token",
            RouterBaseUrl = "https://router.huggingface.co/v1/"
        }, "meta-llama/llama-4-scout");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        handler.LastRequestUri!.ToString().Should().Be("https://router.huggingface.co/v1/chat/completions");
        handler.LastAuthorization.Should().Be("Bearer hf-token");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenTokenMissing_BeforeSend()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("HuggingFace:Token is required.");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenModelMissing()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, null);

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Hugging Face chat requires a model identifier.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsDescriptiveError_OnNonSuccess()
    {
        var handler = new HfCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("denied", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Hugging Face chat request failed (403): denied");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenBodyNull()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse("null"));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Hugging Face chat response was empty.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenChoicesMissing()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse("""{"usage":{"total_tokens":1}}"""));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Hugging Face response did not contain choices.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_SerializesSamplingAndExtensions()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "low",
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["temperature"] = 0.4,
                ["top_p"] = 0.7,
                ["min_p"] = 0.05
            }));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.4);
        json.RootElement.GetProperty("top_p").GetDouble().Should().Be(0.7);
        json.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("low");
        json.RootElement.GetProperty("min_p").GetDouble().Should().Be(0.05);
    }

    [TestMethod]
    public async Task GetCompletionAsync_MultiPartMessage_SerializesContentAsArray()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

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
    public async Task GetCompletionAsync_ToolMessage_SerializesIdAndName()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage("call_9", "lookup", [new ChatContent("the result")])
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var toolMsg = json.RootElement.GetProperty("messages")[1];
        toolMsg.GetProperty("role").GetString().Should().Be("tool");
        toolMsg.GetProperty("tool_call_id").GetString().Should().Be("call_9");
        toolMsg.GetProperty("name").GetString().Should().Be("lookup");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantToolCalls_SerializeArguments()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var objCall = new ChatToolCall
        {
            Id = "c1",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "objfn", Arguments = JsonSerializer.SerializeToElement(new { a = 1 }) }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent> { new("calling") }, new List<ChatToolCall> { objCall })
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("messages")[1].GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("arguments").GetString().Should().Be("{\"a\":1}");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsAndLogs_OnUnmappableContent()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, new List<ChatContent> { new() })],
            model: null);

        var act = () => client.GetCompletionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A message content part could not be mapped for the Hugging Face chat provider*");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsToolCallsAndUsage()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": { "content": "ok", "tool_calls": [ { "id": "c1", "type": "function", "function": { "name": "f", "arguments": "{\"x\":2}" } } ] },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 6, "completion_tokens": 4, "total_tokens": 10 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().Be("ok");
        response.FirstChoice.Message.ToolCalls![0].Function.Arguments.GetProperty("x").GetInt32().Should().Be(2);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.TotalTokens.Should().Be(10);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ExtractsArrayContent()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(
            """
            { "choices": [ { "message": { "content": [ { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ] }, "finish_reason": "stop" } ] }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().Be("ab");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullContent_YieldsEmptyMessage()
    {
        var handler = new HfCapturingHandler(_ => JsonResponse(
            """{ "choices": [ { "message": { "content": null }, "finish_reason": "stop" } ] }"""));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.Message.GetText().Should().BeEmpty();
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
        var handler = new HfCapturingHandler(_ => JsonResponse(
            $$"""{ "choices": [ { "message": { "content": "x" }, "finish_reason": "{{upstream}}" } ] }"""));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.FinishReason.Should().Be(expected);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_AggregatesTextToolCallsUsage()
    {
        var handler = new HfCapturingHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"role":"assistant","content":"Hi"}}]}

            data: {"choices":[{"delta":{"content":" there","tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"math","arguments":"{\"n\":4}"}}]}}],"usage":{"prompt_tokens":2,"completion_tokens":2,"total_tokens":4}}

            data: {"choices":[{"finish_reason":"tool_calls"}]}

            data: [DONE]

            """));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        deltas.Should().Equal("Hi", " there");
        response.FirstChoice!.Message.GetText().Should().Be("Hi there");
        response.FirstChoice.Message.ToolCalls![0].Function.Name.Should().Be("math");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("n").GetInt32().Should().Be(4);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.TotalTokens.Should().Be(4);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ThrowsDescriptiveError_OnNonSuccess()
    {
        var handler = new HfCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("bad gateway", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        var act = () => client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null), _ => { });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Hugging Face chat stream failed (502): bad gateway");
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

    private sealed class HfCapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
