using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAI;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second-wave coverage for <see cref="OpenAiChatClient"/> exercising tool-argument normalization
/// for null/undefined and non-object value kinds, developer role serialization, and response message
/// content delivered as an array of structured parts (text / image_url object / image_url string).
/// </summary>
[TestClass]
public sealed class OpenAiChatClientDeepTests2
{
    private static IChatCompletionClient CreateClient(CapturingHandler handler, HttpClient httpClient)
    {
        var config = new AzureOpenAiConfig
        {
            ApiKey = "sk-test-key",
            ResourceName = null,
            ApiVersion = null,
            DeploymentId = "gpt-4o-mini"
        };
        return new OpenAiChatClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);
    }

    private static string ChatCompletionJson(string content = "ok") =>
        $$"""
        {
          "id": "chatcmpl-1", "object": "chat.completion", "created": 1700000000, "model": "gpt-4o-mini",
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "{{content}}" }, "finish_reason": "stop" } ],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;

    [TestMethod]
    public async Task GetCompletionAsync_NullArgumentsToolCall_NormalizesToEmptyObject()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var nullArgsToolCall = new ChatToolCall
        {
            Id = "call_null",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "f", Arguments = default }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("calling")], [nullArgsToolCall])
            ],
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var args = body.RootElement.GetProperty("messages")[1]
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
        args.ValueKind.Should().Be(JsonValueKind.String);
        args.GetString().Should().Be("{}");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NumberArgumentsToolCall_UsesRawText()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var numberArgsToolCall = new ChatToolCall
        {
            Id = "call_num",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "f", Arguments = JsonSerializer.SerializeToElement(42) }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "go"),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("calling")], [numberArgsToolCall])
            ],
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("messages")[1]
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments")
            .GetString().Should().Be("42");
    }

    [TestMethod]
    public async Task GetCompletionAsync_DeveloperRole_SerializesDeveloper()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.Developer, "be terse"),
                new ChatMessage(ChatRole.User, "hi")
            ],
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("developer");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ArrayResponseContent_ExtractsTextAndImageParts()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "chatcmpl-arr", "object": "chat.completion", "created": 1700000000, "model": "gpt-4o-mini",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": [
                      { "type": "text", "text": "hello" },
                      { "type": "image_url", "image_url": { "url": "https://img/x.png" } },
                      { "type": "image_url", "image_url": "https://img/y.png" }
                    ]
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: "gpt-4o-mini"));

        var content = response.FirstChoice!.Message.Content;
        content.Should().Contain(c => c.IsText && c.Text == "hello");
        content.Should().Contain(c => c.IsImage && c.ImageUrl!.Url == "https://img/x.png");
        content.Should().Contain(c => c.IsImage && c.ImageUrl!.Url == "https://img/y.png");
    }
}
