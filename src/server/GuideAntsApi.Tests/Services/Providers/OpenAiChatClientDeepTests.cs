using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAI;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Deterministic HTTP-handler coverage for <see cref="AntRunner.Chat.OpenAI.OpenAiChatClient"/> driven through
/// the real OpenAI-DotNet transport. Each test asserts request shape (URL / auth / serialized body) and the
/// mapped response.
/// </summary>
[TestClass]
public sealed class OpenAiChatClientDeepTests
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

    private static ChatCompletionRequest TextRequest(
        string model = "gpt-4o-mini",
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

    private static string ChatCompletionJson(
        string content = "Hello world",
        string finishReason = "stop") =>
        $$"""
        {
          "id": "chatcmpl-1",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "gpt-4o-mini",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "{{content}}" },
              "finish_reason": "{{finishReason}}"
            }
          ],
          "usage": {
            "prompt_tokens": 5,
            "completion_tokens": 2,
            "total_tokens": 7,
            "prompt_tokens_details": { "cached_tokens": 1 }
          }
        }
        """;

    [TestMethod]
    public async Task GetCompletionAsync_TextHappyPath_PostsChatCompletionsAndMapsResponse()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var response = await client.GetCompletionAsync(TextRequest());

        handler.LastRequestUri!.ToString().Should().EndWith("/chat/completions");
        handler.LastRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequestHeaders.Authorization.Parameter.Should().Be("sk-test-key");

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        var messages = body.RootElement.GetProperty("messages");
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[1].GetProperty("role").GetString().Should().Be("user");

        response.FirstChoice!.Message.GetText().Should().Be("Hello world");
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage!.PromptTokens.Should().Be(5);
        response.Usage.CompletionTokens.Should().Be(2);
        response.Usage.TotalTokens.Should().Be(7);
        response.Usage.PromptTokensDetails!.CachedTokens.Should().Be(1);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolCallResponse_MapsToolCalls()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "id": "chatcmpl-2",
              "object": "chat.completion",
              "created": 1700000000,
              "model": "gpt-4o-mini",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": { "name": "lookup_weather", "arguments": "{\"city\":\"Boston\"}" }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 8, "completion_tokens": 4, "total_tokens": 12 }
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
                    JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""")))
            ],
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()
            .Should().Be("lookup_weather");

        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
        var toolCall = response.FirstChoice.Message.ToolCalls![0];
        toolCall.Id.Should().Be("call_1");
        toolCall.Function.Name.Should().Be("lookup_weather");
        toolCall.Function.Arguments.GetRawText().Should().Contain("Boston");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithAssistantToolCallsAndToolResult_SerializesMessages()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson("done")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        // Object-shaped arguments (Anthropic origin) must be normalized to a JSON string for OpenAI.
        var objectArgsToolCall = new ChatToolCall
        {
            Id = "call_obj",
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
                new ChatMessage(ChatRole.Assistant, [new ChatContent("checking")], [objectArgsToolCall]),
                new ChatMessage("call_obj", "lookup_weather", [new ChatContent("72F")])
            ],
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var messages = body.RootElement.GetProperty("messages");

        var assistant = messages[1];
        assistant.GetProperty("role").GetString().Should().Be("assistant");
        var serializedArgs = assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
        serializedArgs.ValueKind.Should().Be(JsonValueKind.String);
        serializedArgs.GetString().Should().Contain("Boston");

        var toolMessage = messages[2];
        toolMessage.GetProperty("role").GetString().Should().Be("tool");
        toolMessage.GetProperty("tool_call_id").GetString().Should().Be("call_obj");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithStringArgumentsToolCall_PreservesArgumentsString()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson("done")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        var stringArgsToolCall = new ChatToolCall
        {
            Id = "call_str",
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "lookup_weather",
                Arguments = JsonSerializer.SerializeToElement("{\"city\":\"Paris\"}")
            }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Weather?"),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("checking")], [stringArgsToolCall])
            ],
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var serializedArgs = body.RootElement.GetProperty("messages")[1]
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
        serializedArgs.ValueKind.Should().Be(JsonValueKind.String);
        serializedArgs.GetString().Should().Contain("Paris");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithImageContent_SerializesImageUrlPart()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
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
            model: "gpt-4o-mini"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var raw = body.RootElement.GetRawText();
        raw.Should().Contain("image_url");
        raw.Should().Contain("https://example.test/cat.png");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithSamplingParameters_ProjectsTemperatureAndTopP()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(TextRequest(sampling: new Dictionary<string, double>
        {
            ["temperature"] = 0.4,
            ["top_p"] = 0.7
        }));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("temperature").GetDouble().Should().BeApproximately(0.4, 1e-9);
        body.RootElement.GetProperty("top_p").GetDouble().Should().BeApproximately(0.7, 1e-9);
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithUnprojectableSamplingKey_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(TextRequest(sampling: new Dictionary<string, double>
        {
            ["top_k"] = 50
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*top_k*OpenAI Chat*");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithReasoningModel_IncludesReasoningEffort()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(TextRequest(model: "gpt-5", reasoningEffort: "high"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [TestMethod]
    public async Task GetCompletionAsync_WithNonReasoningModel_OmitsReasoningEffort()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(ChatCompletionJson()));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(handler, httpClient);

        await client.GetCompletionAsync(TextRequest(model: "gpt-4o-mini", reasoningEffort: "high"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
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
        handler.LastRequestUri!.ToString().Should().EndWith("/chat/completions");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_AggregatesSseDeltasAndUsage()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(
            """
            data: {"id":"chatcmpl-s","object":"chat.completion.chunk","created":1700000000,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"},"finish_reason":null}]}

            data: {"id":"chatcmpl-s","object":"chat.completion.chunk","created":1700000000,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}

            data: {"id":"chatcmpl-s","object":"chat.completion.chunk","created":1700000000,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"id":"chatcmpl-s","object":"chat.completion.chunk","created":1700000000,"model":"gpt-4o-mini","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: [DONE]

            """));
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

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();

        deltas.Should().Equal("Hel", "lo");
        response.FirstChoice!.Message.GetText().Should().Be("Hello");
        response.FirstChoice.FinishReason.Should().Be("stop");
        response.Usage!.TotalTokens.Should().Be(5);
    }
}
