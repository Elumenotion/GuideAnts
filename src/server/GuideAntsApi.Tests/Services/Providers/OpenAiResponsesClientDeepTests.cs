using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAI;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Deterministic HTTP-handler coverage for <see cref="AntRunner.Chat.OpenAI.OpenAiResponsesClient"/> driven
/// through the stateless Responses streaming transport. The terminal <c>response.completed</c> SSE event is
/// authoritative; a provider response retrieval request is never made.
/// </summary>
[TestClass]
public sealed class OpenAiResponsesClientDeepTests
{
    private const string CreatedSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\"," +
        "\"status\":\"in_progress\",\"model\":\"gpt-4o\",\"output\":[]}}\n\n";

    private const string DefaultUsageJson =
        "{\"input_tokens\":4,\"output_tokens\":7,\"total_tokens\":11,\"input_token_details\":{\"cached_tokens\":1}}";

    private static IChatCompletionClient CreateClient(CapturingHandler handler, HttpClient httpClient)
    {
        var config = new AzureOpenAiConfig
        {
            ApiKey = "sk-test-key",
            ResourceName = null,
            ApiVersion = null,
            DeploymentId = "gpt-4o"
        };
        return new OpenAiResponsesClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);
    }

    private static ChatCompletionRequest TextRequest(
        string model = "gpt-4o",
        string? reasoningEffort = null,
        IReadOnlyDictionary<string, double>? sampling = null) =>
        new(
            messages:
            [
                new ChatMessage(ChatRole.System, "You are helpful."),
                new ChatMessage(ChatRole.User, "Hello")
            ],
            model: model,
            reasoningEffort: reasoningEffort,
            samplingParameters: sampling);

    private static string AssistantTextItem(string text) =>
        "{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"completed\",\"role\":\"assistant\"," +
        "\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}";

    private static string CompletedResponseJson(string outputItemsJson, string? usageJson = null) =>
        "{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-4o\",\"output\":[" +
        outputItemsJson + "],\"usage\":" + (usageJson ?? DefaultUsageJson) + "}";

    private static Func<HttpRequestMessage, HttpResponseMessage> Responder(
        string outputItemsJson,
        string? usageJson = null,
        string? streamSse = null)
    {
        var completedJson = CompletedResponseJson(outputItemsJson, usageJson);
        var terminalSse =
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":" + completedJson + "}\n\n";
        var sse = (streamSse ?? CreatedSse) + terminalSse;
        return request => request.Method == HttpMethod.Get
            ? ChatHttpResponses.Json(
                """{"error":{"message":"response retrieval is forbidden"}}""",
                HttpStatusCode.NotFound)
            : ChatHttpResponses.Sse(sse);
    }

    [TestMethod]
    public async Task GetCompletionAsync_TextHappyPath_PostsResponsesAndMapsResponse()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("Hello world")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        postRequest.Uri!.ToString().Should().EndWith("/responses");
        postRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        postRequest.Headers.Authorization.Parameter.Should().Be("sk-test-key");

        using var body = JsonDocument.Parse(postRequest.Body);
        body.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o");
        body.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
        body.RootElement.TryGetProperty("previous_response_id", out _).Should().BeFalse();
        body.RootElement.GetProperty("truncation").GetString().Should().Be("disabled");
        body.RootElement.GetProperty("input").GetArrayLength().Should().Be(2);
        handler.RequestCount.Should().Be(1);

        response.FirstChoice!.Message.GetText().Should().Be("Hello world");
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage!.PromptTokens.Should().Be(4);
        response.Usage.CompletionTokens.Should().Be(7);
        response.Usage.TotalTokens.Should().Be(11);
        response.Usage.PromptTokensDetails!.CachedTokens.Should().Be(1);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolCallResponse_MapsToolCallsAndFinishReason()
    {
        var toolItem = "{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\"," +
            "\"name\":\"lookup_weather\",\"arguments\":\"{\\\"city\\\":\\\"Boston\\\"}\",\"status\":\"completed\"}";
        var outputItems = AssistantTextItem("Checking") + "," + toolItem;
        var handler = new CapturingHandler(Responder(outputItems));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "Weather?")],
            tools:
            [
                new ChatToolDefinition(new ChatFunctionDefinition(
                    "lookup_weather",
                    "Look up weather",
                    JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""")))
            ],
            model: "gpt-4o"));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("lookup_weather");

        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        var toolCall = response.FirstChoice.Message.ToolCalls![0];
        toolCall.Id.Should().Be("call_1");
        toolCall.Function.Name.Should().Be("lookup_weather");
        toolCall.Function.Arguments.GetRawText().Should().Contain("Boston");
        handler.RequestCount.Should().Be(1);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ReasoningItemInOutput_MapsThinkingBlock()
    {
        var reasoningItem = "{\"type\":\"reasoning\",\"id\":\"rs_1\"," +
            "\"summary\":[{\"type\":\"summary_text\",\"text\":\"Step by step\"}]}";
        var outputItems = reasoningItem + "," + AssistantTextItem("Final answer");
        var handler = new CapturingHandler(Responder(outputItems));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        response.FirstChoice!.Message.GetText().Should().Be("Final answer");
        response.FirstChoice.Message.ThinkingBlocks.Should().NotBeNull();
        response.FirstChoice.Message.ThinkingBlocks!.Should().HaveCount(1);
        response.FirstChoice.Message.ThinkingBlocks[0].Thinking.Should().Contain("Step by step");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithReasoningModel_IncludesReasoningInRequest()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(TextRequest(model: "o3", reasoningEffort: "high"));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        body.RootElement.TryGetProperty("reasoning", out var reasoning).Should().BeTrue();
        reasoning.GetProperty("effort").GetString().Should().Be("high");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithNonReasoningModel_OmitsReasoningInRequest()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(TextRequest(model: "gpt-4o", reasoningEffort: "high"));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        body.RootElement.TryGetProperty("reasoning", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithSamplingParameters_ProjectsTemperatureAndTopP()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(TextRequest(sampling: new Dictionary<string, double>
        {
            ["temperature"] = 0.25,
            ["top_p"] = 0.6
        }));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        body.RootElement.GetProperty("temperature").GetDouble().Should().BeApproximately(0.25, 1e-9);
        body.RootElement.GetProperty("top_p").GetDouble().Should().BeApproximately(0.6, 1e-9);
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithUnprojectableSamplingKey_Throws()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(TextRequest(sampling: new Dictionary<string, double>
        {
            ["min_p"] = 0.1
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*min_p*OpenAI Responses*");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithToolMessagesAndAssistantToolCalls_SerializesInputItems()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("done")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var toolCall = new ChatToolCall
        {
            Id = "call_x",
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
                new ChatMessage(ChatRole.User, "Weather?"),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("checking")], [toolCall]),
                new ChatMessage("call_x", "lookup_weather", [new ChatContent("72F")])
            ],
            model: "gpt-4o"));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        var raw = body.RootElement.GetProperty("input").GetRawText();
        raw.Should().Contain("function_call");
        raw.Should().Contain("function_call_output");
        raw.Should().Contain("call_x");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithImageContent_SerializesInputImage()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new("What is this?"),
                    new(new ChatImageUrl("https://example.test/cat.png"))
                })
            ],
            model: "gpt-4o"));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        var raw = body.RootElement.GetProperty("input").GetRawText();
        raw.Should().Contain("https://example.test/cat.png");
        raw.Should().Contain("input_image");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonSuccessStatus_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{ "error": { "message": "bad", "type": "invalid_request_error" } }""",
            HttpStatusCode.BadRequest));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(TextRequest());

        await act.Should().ThrowAsync<Exception>();
        handler.LastRequestUri!.ToString().Should().EndWith("/responses");
    }

    [TestMethod]
    public async Task GetCompletionAsync_StreamWithoutTerminalEvent_ThrowsWithoutRetrieval()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(CreatedSse));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(TextRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*without a terminal response.completed event*");
        handler.RequestCount.Should().Be(1);
    }

    [TestMethod]
    public async Task GetCompletionAsync_AzureUsesResourceEndpointAndApiKeyHeader()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var config = new AzureOpenAiConfig
        {
            ApiKey = "azure-test-key",
            ResourceName = "test-resource",
            ApiVersion = "2025-04-01-preview",
            DeploymentId = "gpt-4o"
        };
        var client = new OpenAiResponsesClientFactory(
                new StaticHttpClientFactory(httpClient),
                config)
            .CreateClient(null, httpClient);

        await client.GetCompletionAsync(TextRequest());

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri!.ToString().Should().Be(
            "https://test-resource.openai.azure.com/openai/responses?api-version=2025-04-01-preview");
        request.Headers.Authorization.Should().BeNull();
        request.Headers.TryGetValues("api-key", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("azure-test-key");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_EmitsTextDeltasAndMapsFinalResponse()
    {
        var sse =
            CreatedSse +
            "event: response.output_item.added\n" +
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\"," +
            "\"id\":\"msg_1\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
            "event: response.content_part.added\n" +
            "data: {\"type\":\"response.content_part.added\",\"item_id\":\"msg_1\",\"output_index\":0," +
            "\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\",\"annotations\":[]}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0," +
            "\"content_index\":0,\"delta\":\"Hel\"}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0," +
            "\"content_index\":0,\"delta\":\"lo\"}\n\n" +
            "event: response.output_text.done\n" +
            "data: {\"type\":\"response.output_text.done\",\"item_id\":\"msg_1\",\"output_index\":0," +
            "\"content_index\":0,\"text\":\"Hello\"}\n\n";

        var usage = "{\"input_tokens\":3,\"output_tokens\":2,\"total_tokens\":5}";
        var handler = new CapturingHandler(Responder(AssistantTextItem("Hello"), usage, sse));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            TextRequest(),
            chunk =>
            {
                var content = chunk.FirstChoice?.Delta.Content;
                if (!string.IsNullOrEmpty(content))
                {
                    deltas.Add(content);
                }
            });

        // Streaming deltas are emitted directly; the terminal event supplies final text and usage.
        deltas.Should().NotBeEmpty();
        deltas[0].Should().Be("Hel");
        deltas[1].Should().Be("lo");
        response.FirstChoice!.Message.GetText().Should().Be("Hello");
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage!.TotalTokens.Should().Be(5);
        handler.RequestCount.Should().Be(1);
    }
}
