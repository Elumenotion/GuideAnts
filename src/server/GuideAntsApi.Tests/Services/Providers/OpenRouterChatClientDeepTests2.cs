using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenRouter;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second-wave coverage for <see cref="OpenRouterChatClient"/>: developer role, tool-definition guard,
/// response content variants (array/object/image), finish-reason normalization, cached-usage details,
/// and streaming delta-without-content / no-choice branches.
/// </summary>
[TestClass]
public sealed class OpenRouterChatClientDeepTests2
{
    private static OpenRouterChatClient Client(CapturingHandler handler, HttpClient httpClient) =>
        new(httpClient, new OpenRouterChatConfig { ApiKey = "or-key", HttpReferer = "https://app.test", AppTitle = "App" }, "anthropic/claude");

    private static ChatCompletionRequest UserRequest() =>
        new(messages: [new ChatMessage(ChatRole.User, "hi")], model: "anthropic/claude");

    [TestMethod]
    public async Task GetCompletionAsync_SetsRefererAndTitleHeaders_AndSerializesDeveloperRole()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.Developer, "be terse"),
                new ChatMessage(ChatRole.User, "hi")
            ],
            model: "anthropic/claude"));

        handler.LastRequestHeaders.TryGetValues("HTTP-Referer", out var referer).Should().BeTrue();
        referer!.Single().Should().Be("https://app.test");
        handler.LastRequestHeaders.TryGetValues("X-Title", out _).Should().BeTrue();

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("developer");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullFunctionTool_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json("{}"));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(null!)],
            model: "anthropic/claude"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*require a function definition*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MissingModel_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json("{}"));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, null);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a model identifier*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ResponseMessageNull_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*choice message is required*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ArrayContentWithTextAndImage_Extracted()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "choices": [
                {
                  "message": { "content": [
                    "skip-non-object",
                    { "type": "text", "text": "hello" },
                    { "type": "image_url", "image_url": { "url": "https://img/x.png" } }
                  ] },
                  "finish_reason": "stop"
                }
              ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        var content = response.FirstChoice!.Message.Content;
        content.Should().Contain(c => c.IsText && c.Text == "hello");
        content.Should().Contain(c => c.IsImage && c.ImageUrl!.Url == "https://img/x.png");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ObjectContentWithImage_Extracted()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "choices": [
                { "message": { "content": { "image_url": { "url": "https://img/y.png" } } }, "finish_reason": "stop" }
              ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.FirstChoice!.Message.Content.Single().ImageUrl!.Url.Should().Be("https://img/y.png");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ObjectContentWithText_Extracted()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"content":{"text":"obj-text"}},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.FirstChoice!.Message.GetText().Should().Be("obj-text");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MissingFinishReason_MapsToNull()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"content":"x"}}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.FirstChoice!.FinishReason.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsCachedPromptTokens()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "choices": [ { "message": { "content": "x" }, "finish_reason": "stop" } ],
              "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15, "prompt_tokens_details": { "cached_tokens": 4 } }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest());

        response.Usage!.PromptTokensDetails!.CachedTokens.Should().Be(4);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_DeltaWithoutContentButFinishReason_SetsFinish()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest(), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("hi");
        response.FirstChoice.FinishReason.Should().Be("stop");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_NoChoicesChunk_Ignored()
    {
        var sse =
            "data: {\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest(), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("hi");
        response.Usage!.TotalTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ArrayDeltaContentAndToolCalls_Aggregate()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"par\"}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":{\"text\":\"tial\"}}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"f\",\"arguments\":\"{\\\"x\\\":2}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest(), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("partial");
        response.FirstChoice.Message.ToolCalls!.Single().Function.Arguments.GetProperty("x").GetInt32().Should().Be(2);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
    }
}
