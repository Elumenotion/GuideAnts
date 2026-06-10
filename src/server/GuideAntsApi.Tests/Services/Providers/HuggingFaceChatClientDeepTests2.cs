using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.HuggingFace;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second-wave coverage for <see cref="HuggingFaceChatClient"/>: developer role, tool-definition guard,
/// assistant message with tool calls but empty content, response content array/object variants,
/// null finish reason, and streaming delta-without-content / no-choice / array-content branches.
/// </summary>
[TestClass]
public sealed class HuggingFaceChatClientDeepTests2
{
    private static HuggingFaceChatClient Client(CapturingHandler handler, HttpClient httpClient) =>
        new(httpClient, new HuggingFaceChatConfig { Token = "hf", RouterBaseUrl = "https://router.huggingface.co/v1" }, "meta/model");

    private static ChatCompletionRequest UserRequest() =>
        new(messages: [new ChatMessage(ChatRole.User, "hi")], model: "meta/model");

    [TestMethod]
    public async Task GetCompletionAsync_NullFunctionTool_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json("{}"));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(null!)],
            model: "meta/model"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*require a function definition*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_DeveloperRole_SerializesDeveloper()
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
            model: "meta/model"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("developer");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantToolCallsWithEmptyContent_SkipsContentMapping()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var toolCall = new ChatToolCall
        {
            Id = "c1",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "f", Arguments = JsonSerializer.SerializeToElement(new { a = 1 }) }
        };

        // Empty content list + tool calls exercises the GetMappableContent count==0 early return.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent>(), [toolCall])
            ],
            model: "meta/model"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var assistant = body.RootElement.GetProperty("messages")[1];
        assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString().Should().Be("f");
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
    public async Task GetCompletionAsync_ArrayContentWithImageAndNonObject_Extracted()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "choices": [
                {
                  "message": { "content": [
                    "non-object-skip",
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
    public async Task StreamCompletionAsync_ArrayAndObjectDeltaContent_Aggregate()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"par\"}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":{\"text\":\"tial\"}}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest(), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("partial");
    }
}
