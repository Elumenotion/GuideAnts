using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAiCompatible;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.OpenAiCompatible;

[TestClass]
public sealed class OpenAiCompatibleChatClientTests
{
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

    private static OpenAiCompatibleChatClient CreateClient(HttpClient httpClient, string apiKey = "row-key") =>
        new(httpClient, new OpenAiCompatibleChatConfig
        {
            BaseUrl = "http://localhost:8000/v1",
            ApiKey = apiKey
        }, "my-model");

    [TestMethod]
    public async Task GetCompletionAsync_PostsToChatCompletions_AndStripsTrailingSlash()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient, new OpenAiCompatibleChatConfig
        {
            BaseUrl = "http://localhost:8000/v1/",
            ApiKey = "k"
        }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        handler.LastRequestUri!.ToString().Should().Be("http://localhost:8000/v1/chat/completions");
    }

    [TestMethod]
    public async Task GetCompletionAsync_BodyCarriesModelMessagesAndTools()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var tool = new ChatToolDefinition(new ChatFunctionDefinition("add", "adds", JsonNode.Parse("""{"type":"object"}""")));
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [tool],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        var root = json.RootElement;
        root.GetProperty("model").GetString().Should().Be("my-model");
        root.GetProperty("messages").GetArrayLength().Should().Be(1);
        root.GetProperty("tools").GetArrayLength().Should().Be(1);
        root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString().Should().Be("add");
    }

    [TestMethod]
    public async Task GetCompletionAsync_SendsBearerOnly_WhenKeySet()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, apiKey: "my-secret");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        handler.LastRequestHeaders.TryGetValues("Authorization", out var auth).Should().BeTrue();
        auth!.Single().Should().Be("Bearer my-secret");
    }

    [TestMethod]
    public async Task GetCompletionAsync_SendsNoAuthHeader_WhenKeyless()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, apiKey: "");

        await client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        handler.LastRequestHeaders.TryGetValues("Authorization", out var auth).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsDescriptiveError_OnNonSuccess()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream down", Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("OpenAI-compatible chat request failed (502): upstream down");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenModelMissing()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient, new OpenAiCompatibleChatConfig { BaseUrl = "http://localhost:8000/v1" }, null);

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("OpenAI-compatible chat requires a model identifier.");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ParsesSse_ToolCallChunksAndDone()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}" + "\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\",\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"type\":\"function\",\"function\":{\"name\":\"math\",\"arguments\":\"{\\\"n\\\":\"}}]}}]}" + "\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"7}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}" + "\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => SseResponse(sse));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null),
            onChunk: _ => { });

        var message = response.FirstChoice!.Message;
        message.GetText().Should().Be("Hello");
        message.ToolCalls.Should().ContainSingle();
        message.ToolCalls![0].Id.Should().Be("call_a");
        message.ToolCalls[0].Function.Name.Should().Be("math");
        message.ToolCalls[0].Function.Arguments.GetProperty("n").GetInt32().Should().Be(7);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.TotalTokens.Should().Be(5);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ParsesPlainTextStream()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var chunks = new List<string>();
        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "hi")], model: null),
            onChunk: chunk => chunks.Add(chunk.FirstChoice!.Delta.Content ?? string.Empty));

        response.FirstChoice!.Message.GetText().Should().Be("done");
        response.FirstChoice.FinishReason.Should().Be("stop");
        chunks.Should().ContainSingle().Which.Should().Be("done");
    }

    [TestMethod]
    public void SupportsToolChoiceNone_IsTrue()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        CreateClient(httpClient).SupportsToolChoiceNone.Should().BeTrue();
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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