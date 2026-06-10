using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAI;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second-wave coverage for <see cref="OpenAiResponsesClient"/> targeting mapper branches not exercised
/// by the first deep file: null-function tool guard, tool-output id validation, null tool-call arguments,
/// refusal content mapping, function_call_output in the output list, reasoning content (vs summary), and
/// null-usage mapping.
/// </summary>
[TestClass]
public sealed class OpenAiResponsesClientDeepTests2
{
    private const string CreatedSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\"," +
        "\"status\":\"in_progress\",\"model\":\"gpt-4o\",\"output\":[]}}\n\n";

    private const string DefaultUsageJson =
        "{\"input_tokens\":4,\"output_tokens\":7,\"total_tokens\":11}";

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

    private static ChatCompletionRequest TextRequest() =>
        new(messages: [new ChatMessage(ChatRole.User, "Hello")], model: "gpt-4o");

    private static string AssistantTextItem(string text) =>
        "{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"completed\",\"role\":\"assistant\"," +
        "\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}";

    private static string CompletedResponseJson(string outputItemsJson, string? usageJson = null)
    {
        var usagePart = usageJson == "null" ? "null" : (usageJson ?? DefaultUsageJson);
        return "{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-4o\",\"output\":[" +
            outputItemsJson + "],\"usage\":" + usagePart + "}";
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Responder(string outputItemsJson, string? usageJson = null)
    {
        var getJson = CompletedResponseJson(outputItemsJson, usageJson);
        return request => request.Method == HttpMethod.Get
            ? ChatHttpResponses.Json(getJson)
            : ChatHttpResponses.Sse(CreatedSse);
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullFunctionTool_Throws()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(null!)],
            model: "gpt-4o"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Function tool definition is required*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolMessageMissingId_Throws()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "hi"),
                new ChatMessage("   ", "lookup", [new ChatContent("result")])
            ],
            model: "gpt-4o"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ToolCallId*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantToolCallNullArguments_SerializesEmptyObject()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("done")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var toolCall = new ChatToolCall
        {
            Id = "call_n",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "f", Arguments = default }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("calling")], [toolCall])
            ],
            model: "gpt-4o"));

        var postRequest = handler.Requests.First(r => r.Uri!.AbsoluteUri.EndsWith("/responses"));
        using var body = JsonDocument.Parse(postRequest.Body);
        body.RootElement.GetProperty("input").GetRawText().Should().Contain("function_call");
    }

    [TestMethod]
    public async Task GetCompletionAsync_RefusalContent_MapsToText()
    {
        var refusalItem =
            "{\"type\":\"message\",\"id\":\"msg_r\",\"status\":\"completed\",\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"refusal\",\"refusal\":\"I cannot help with that\"}]}";
        var handler = new CapturingHandler(Responder(refusalItem));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        response.FirstChoice!.Message.GetText().Should().Contain("I cannot help with that");
    }

    [TestMethod]
    public async Task GetCompletionAsync_FunctionCallOutputInOutputList_IsIgnored()
    {
        var outputItem =
            "{\"type\":\"function_call_output\",\"id\":\"fco_1\",\"call_id\":\"call_1\",\"output\":\"42\"}";
        var outputItems = AssistantTextItem("answer") + "," + outputItem;
        var handler = new CapturingHandler(Responder(outputItems));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        response.FirstChoice!.Message.GetText().Should().Be("answer");
        response.FirstChoice.FinishReason.Should().Be("stop");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ReasoningItemWithContent_MapsThinkingBlock()
    {
        var reasoningItem =
            "{\"type\":\"reasoning\",\"id\":\"rs_2\",\"content\":[{\"type\":\"reasoning_text\",\"text\":\"deep reasoning\"}]}";
        var outputItems = reasoningItem + "," + AssistantTextItem("final");
        var handler = new CapturingHandler(Responder(outputItems));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        response.FirstChoice!.Message.GetText().Should().Be("final");
        response.FirstChoice.Message.ThinkingBlocks.Should().NotBeNull();
        response.FirstChoice.Message.ThinkingBlocks!.Single().Thinking.Should().Contain("deep reasoning");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullUsage_MapsToNullUsage()
    {
        var handler = new CapturingHandler(Responder(AssistantTextItem("ok"), usageJson: "null"));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        response.FirstChoice!.Message.GetText().Should().Be("ok");
        response.Usage.Should().BeNull();
    }
}
