using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.GoogleGemini;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class GoogleGeminiChatClientDeepTests
{
    [TestMethod]
    public async Task GetCompletionAsync_NormalizesBareModelName_AndOverridesDefault()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-default");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "gemini-2.5-flash"));

        handler.LastRequestUri!.ToString().Should()
            .Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent");
    }

    [TestMethod]
    public async Task GetCompletionAsync_KeepsModelsPrefix_WhenAlreadyQualified()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "models/gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        handler.LastRequestUri!.ToString().Should()
            .Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenModelMissing()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, null);

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini chat requires a model identifier.");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenApiKeyMissing_BeforeSending()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "" }, "gemini-2.5-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("GoogleGeminiApi:ApiKey is required.");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsDescriptiveError_OnNonSuccessStatus()
    {
        var handler = new GeminiCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini chat request failed (429): rate limited");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenResponseBodyIsNull()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse("null"));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Google Gemini chat response was empty.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenCandidatesMissing()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse("""{"usageMetadata":{"totalTokenCount":3}}"""));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini response did not contain candidates.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenNoNonSystemMessage()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.System, "system only")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini chat requires at least one non-system message.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenSystemMessageHasNonText()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var request = new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, new List<ChatContent> { new(new ChatImageUrl("data:image/png;base64,AAAA")) }),
                new ChatMessage(ChatRole.User, "hi")
            ],
            model: null);

        var act = () => client.GetCompletionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini system/developer messages only support text content.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsOnUnsupportedContentItem()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, new List<ChatContent> { new() })],
            model: null);

        var act = () => client.GetCompletionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unsupported Google Gemini content item.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsToolsAndSystemInstruction_AndParsesToolCalls()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(
            """
            {
              "candidates": [
                {
                  "content": {
                    "role": "model",
                    "parts": [
                      { "text": "weather coming up" },
                      { "functionCall": { "name": "lookup_weather", "args": { "city": "Boston" } } }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": { "promptTokenCount": 8, "candidatesTokenCount": 3, "totalTokenCount": 11, "cachedContentTokenCount": 2 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "gemini-key" }, "gemini-2.5-pro");

        var response = await client.GetCompletionAsync(ToolRequest(reasoningEffort: null));

        handler.LastRequestHeaders.TryGetValues("x-goog-api-key", out var keys).Should().BeTrue();
        keys!.Single().Should().Be("gemini-key");
        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().Be("You are helpful.");
        json.RootElement.GetProperty("contents")[0].GetProperty("role").GetString().Should().Be("user");
        json.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString()
            .Should().Be("lookup_weather");

        response.FirstChoice!.Message.GetText().Should().Be("weather coming up");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls![0].Function.Name.Should().Be("lookup_weather");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("city").GetString().Should().Be("Boston");
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.PromptTokens.Should().Be(8);
        response.Usage.PromptTokensDetails!.CachedTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsSamplingParameters_IntoGenerationConfig()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["temperature"] = 0.3,
                ["top_p"] = 0.85,
                ["top_k"] = 40
            }));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var gen = json.RootElement.GetProperty("generationConfig");
        gen.GetProperty("temperature").GetDouble().Should().Be(0.3);
        gen.GetProperty("topP").GetDouble().Should().Be(0.85);
        gen.GetProperty("top_k").GetDouble().Should().Be(40);
    }

    [TestMethod]
    public async Task GetCompletionAsync_Gemini25_MapsReasoningEffortToThinkingBudget()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: null, reasoningEffort: "high"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32()
            .Should().Be(24576);
    }

    [TestMethod]
    public async Task GetCompletionAsync_Gemini3_MapsReasoningEffortToThinkingLevel()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-3-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: null, reasoningEffort: "minimal"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString()
            .Should().Be("minimal");
    }

    [TestMethod]
    public async Task GetCompletionAsync_Gemini25_ThrowsOnUnsupportedReasoningEffort()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: null, reasoningEffort: "ultra"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported Google Gemini reasoning_effort 'ultra'.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_Gemini3_ThrowsOnUnsupportedReasoningEffort()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-3-pro");

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: null, reasoningEffort: "none"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported Google Gemini reasoning_effort 'none'.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsInlineImageData_FromDataUrl()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new("look"),
                    new(new ChatImageUrl("data:image/png;base64,AAAA"))
                })
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var parts = json.RootElement.GetProperty("contents")[0].GetProperty("parts");
        parts[0].GetProperty("text").GetString().Should().Be("look");
        parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString().Should().Be("image/png");
        parts[1].GetProperty("inlineData").GetProperty("data").GetString().Should().Be("AAAA");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsFileData_FromRemoteImageUrl()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, new List<ChatContent> { new(new ChatImageUrl("https://cdn.example/x.jpeg")) })],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var part = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        part.GetProperty("fileData").GetProperty("mimeType").GetString().Should().Be("image/jpeg");
        part.GetProperty("fileData").GetProperty("fileUri").GetString().Should().Be("https://cdn.example/x.jpeg");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsToolMessage_ToFunctionResponse()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage("call_1", "lookup_weather", [new ChatContent("""{"temp":72}""")])
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var fnResp = json.RootElement.GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("functionResponse");
        fnResp.GetProperty("name").GetString().Should().Be("lookup_weather");
        fnResp.GetProperty("response").GetProperty("temp").GetInt32().Should().Be(72);
    }

    [TestMethod]
    public async Task GetCompletionAsync_WrapsNonJsonToolResponse()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage("call_1", "lookup_weather", [new ChatContent("not json")])
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var fnResp = json.RootElement.GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("functionResponse");
        fnResp.GetProperty("response").GetProperty("content").GetString().Should().Be("not json");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsForToolMessageMissingFunctionName()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var request = new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Tool, new List<ChatContent> { new("result") })
            ],
            model: null);

        var act = () => client.GetCompletionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini tool messages require FunctionName.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsAssistantToolCalls_AsFunctionCallParts()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var toolCall = new ChatToolCall
        {
            Id = "call_1",
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
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent> { new("calling") }, new List<ChatToolCall> { toolCall })
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var parts = json.RootElement.GetProperty("contents")[1].GetProperty("parts");
        parts[0].GetProperty("text").GetString().Should().Be("calling");
        parts[1].GetProperty("functionCall").GetProperty("name").GetString().Should().Be("lookup_weather");
        parts[1].GetProperty("functionCall").GetProperty("args").GetProperty("city").GetString().Should().Be("Boston");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NormalizesFinishReasonVariants()
    {
        await AssertFinishReason("MAX_TOKENS", "length");
        await AssertFinishReason("SAFETY", "stop");
        await AssertFinishReason("RECITATION", "stop");
        await AssertFinishReason("SOMETHING_NEW", "stop");
    }

    private static async Task AssertFinishReason(string upstream, string expected)
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(
            $$"""
            { "candidates": [ { "content": { "role": "model", "parts": [ { "text": "x" } ] }, "finishReason": "{{upstream}}" } ] }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.FirstChoice!.FinishReason.Should().Be(expected);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ReturnsNullUsage_WhenMetadataAbsent()
    {
        var handler = new GeminiCapturingHandler(_ => JsonResponse(SimpleTextResponse("hello")));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        response.Usage.Should().BeNull();
        response.FirstChoice!.FinishReason.Should().Be("stop");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_UsesSseEndpoint_AggregatesTextAndUsage()
    {
        var handler = new GeminiCapturingHandler(_ => SseResponse(
            """
            data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hi"}]}}]}

            data: {"candidates":[{"content":{"role":"model","parts":[{"text":" there"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":2,"totalTokenCount":4}}

            data: [DONE]

            """));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        handler.LastRequestUri!.ToString().Should().Contain(":streamGenerateContent?alt=sse");
        response.FirstChoice!.Message.GetText().Should().Be("Hi there");
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage!.TotalTokens.Should().Be(4);
        deltas.Should().Equal("Hi", " there");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_AggregatesFunctionCall()
    {
        var handler = new GeminiCapturingHandler(_ => SseResponse(
            """
            data: {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"do_it","args":{"n":1}}}]},"finishReason":"STOP"}]}

            """));
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null),
            _ => { });

        response.FirstChoice!.Message.ToolCalls.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls![0].Function.Name.Should().Be("do_it");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("n").GetInt32().Should().Be(1);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ThrowsDescriptiveError_OnNonSuccessStatus()
    {
        var handler = new GeminiCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("down", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(httpClient, new GoogleGeminiChatConfig { ApiKey = "k" }, "gemini-2.5-pro");

        var act = () => client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null), _ => { });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini chat stream failed (503): down");
    }

    private static ChatCompletionRequest ToolRequest(string? reasoningEffort) =>
        new(
            messages:
            [
                new ChatMessage(ChatRole.System, "You are helpful."),
                new ChatMessage(ChatRole.User, "What's the weather?")
            ],
            tools:
            [
                new ChatToolDefinition(new ChatFunctionDefinition(
                    "lookup_weather",
                    "Look up weather",
                    JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""")))
            ],
            model: null,
            reasoningEffort: reasoningEffort);

    private static string SimpleTextResponse(string text) =>
        $$"""
        { "candidates": [ { "content": { "role": "model", "parts": [ { "text": "{{text}}" } ] }, "finishReason": "STOP" } ] }
        """;

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

    private sealed class GeminiCapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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
