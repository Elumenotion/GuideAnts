using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using AntRunner.Chat.GoogleGemini;
using AntRunner.Chat.HuggingFace;
using AntRunner.Chat.OpenAI;
using AntRunner.Chat.OpenRouter;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class ProviderNativeChatClientTests
{
    [TestMethod]
    public async Task OpenRouter_GetCompletionAsync_UsesChatCompletionsAndMapsToolCalls()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "hello from openrouter",
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "lookup_weather",
                          "arguments": "{\"city\":\"Boston\"}"
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": {
                "prompt_tokens": 5,
                "completion_tokens": 2,
                "total_tokens": 7,
                "prompt_tokens_details": {
                  "cached_tokens": 1
                }
              }
            }
            """));

        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig
            {
                ApiKey = "or-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            "openai/gpt-4o-mini");

        var response = await client.GetCompletionAsync(CreateToolRequest());

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
        handler.LastRequestHeaders.Authorization?.Scheme.Should().Be("Bearer");
        handler.LastRequestHeaders.Authorization?.Parameter.Should().Be("or-key");
        handler.LastRequestHeaders.TryGetValues("HTTP-Referer", out var refererValues).Should().BeTrue();
        refererValues!.Single().Should().Be(OpenRouterAttribution.HttpReferer);

        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("model").GetString().Should().Be("openai/gpt-4o-mini");
        requestJson.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("medium");
        requestJson.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()
            .Should().Be("lookup_weather");

        response.FirstChoice!.Message.GetText().Should().Be("hello from openrouter");
        response.FirstChoice!.Message.ToolCalls.Should().HaveCount(1);
        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.Usage!.PromptTokens.Should().Be(5);
        response.Usage.PromptTokensDetails!.CachedTokens.Should().Be(1);
    }

    [TestMethod]
    public async Task OpenRouter_StreamCompletionAsync_AggregatesSseText()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"role":"assistant","content":"Hel"}}]}

            data: {"choices":[{"delta":{"content":"lo"}}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: {"choices":[{"finish_reason":"stop"}]}

            data: [DONE]

            """));

        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig
            {
                ApiKey = "or-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            "openai/gpt-4o-mini");

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            CreateTextRequest(),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        response.FirstChoice!.Message.GetText().Should().Be("Hello");
        response.FirstChoice!.FinishReason.Should().Be("stop");
        response.Usage!.TotalTokens.Should().Be(5);
        deltas.Should().Equal("Hel", "lo");
    }

    [TestMethod]
    public async Task HuggingFace_GetCompletionAsync_UsesRouterEndpointAndMapsToolCalls()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "hello from hf",
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "lookup_weather",
                          "arguments": "{\"city\":\"Boston\"}"
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": {
                "prompt_tokens": 6,
                "completion_tokens": 4,
                "total_tokens": 10
              }
            }
            """));

        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig
            {
                Token = "hf-token",
                RouterBaseUrl = "https://router.huggingface.co/v1"
            },
            "meta-llama/llama-4-scout");

        var response = await client.GetCompletionAsync(CreateToolRequest());

        handler.LastRequestUri!.ToString().Should().Be("https://router.huggingface.co/v1/chat/completions");
        handler.LastRequestHeaders.Authorization?.Parameter.Should().Be("hf-token");
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("model").GetString().Should().Be("meta-llama/llama-4-scout");
        requestJson.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("system");

        response.FirstChoice!.Message.GetText().Should().Be("hello from hf");
        response.FirstChoice!.Message.ToolCalls.Should().HaveCount(1);
        response.Usage!.TotalTokens.Should().Be(10);
    }

    [TestMethod]
    public async Task HuggingFace_StreamCompletionAsync_AggregatesSseText()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"role":"assistant","content":"Hi"}}]}

            data: {"choices":[{"delta":{"content":" there"}}],"usage":{"prompt_tokens":2,"completion_tokens":2,"total_tokens":4}}

            data: {"choices":[{"finish_reason":"stop"}]}

            data: [DONE]

            """));

        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig
            {
                Token = "hf-token",
                RouterBaseUrl = "https://router.huggingface.co/v1"
            },
            "meta-llama/llama-4-scout");

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            CreateTextRequest(),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        handler.LastRequestUri!.ToString().Should().Be("https://router.huggingface.co/v1/chat/completions");
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        response.FirstChoice!.Message.GetText().Should().Be("Hi there");
        response.Usage!.TotalTokens.Should().Be(4);
        deltas.Should().Equal("Hi", " there");
    }

    [TestMethod]
    public async Task HuggingFace_GetCompletionAsync_WithAssistantToolCallsAndEmptyContentPart_SendsRequest()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [
                {
                  "message": { "content": "done", "role": "assistant" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig
            {
                Token = "hf-token",
                RouterBaseUrl = "https://router.huggingface.co/v1"
            },
            "zai-org/GLM-5.2");

        var toolCall = new ChatToolCall
        {
            Id = "call_1",
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "Media_Creator",
                Arguments = JsonSerializer.SerializeToElement("""{"instructions":"test"}""")
            }
        };

        var request = new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, "You are helpful."),
                new ChatMessage(ChatRole.User, "Recolour the rose."),
                new ChatMessage(ChatRole.Assistant, [new ChatContent("")], [toolCall]),
                new ChatMessage("call_1", "Media_Creator", [new ChatContent("ERROR: tool failed")])
            ],
            model: null);

        var response = await client.GetCompletionAsync(request);

        handler.LastRequestUri.Should().NotBeNull();
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        var assistantMessage = requestJson.RootElement
            .GetProperty("messages")[2];
        assistantMessage.GetProperty("role").GetString().Should().Be("assistant");
        assistantMessage.TryGetProperty("tool_calls", out _).Should().BeTrue();
        if (assistantMessage.TryGetProperty("content", out var contentElement))
        {
            (contentElement.ValueKind == JsonValueKind.Null
                || (contentElement.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(contentElement.GetString())))
                .Should().BeTrue();
        }
        response.FirstChoice!.Message.GetText().Should().Be("done");
    }

    [TestMethod]
    public async Task HuggingFace_GetCompletionAsync_WithUnsupportedContent_ThrowsDescriptiveErrorBeforeSend()
    {
        var handler = new CapturingHandler(_ => JsonResponse("""{"choices":[]}"""));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig
            {
                Token = "hf-token",
                RouterBaseUrl = "https://router.huggingface.co/v1"
            },
            "meta-llama/llama-4-scout");

        var request = new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, [new ChatContent()])
            ],
            model: null);

        Func<Task> act = () => client.GetCompletionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A message content part could not be mapped for the Hugging Face chat provider*");
        handler.LastRequestUri.Should().BeNull();
    }

    [TestMethod]
    public async Task GoogleGemini_GetCompletionAsync_UsesGenerateContentEndpointAndMapsTools()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "candidates": [
                {
                  "content": {
                    "role": "model",
                    "parts": [
                      { "text": "hello from gemini" },
                      {
                        "functionCall": {
                          "name": "lookup_weather",
                          "args": { "city": "Boston" }
                        }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 8,
                "candidatesTokenCount": 3,
                "totalTokenCount": 11,
                "cachedContentTokenCount": 2
              }
            }
            """));

        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(
            httpClient,
            new GoogleGeminiChatConfig
            {
                ApiKey = "gemini-key"
            },
            "gemini-2.5-pro");

        var response = await client.GetCompletionAsync(CreateToolRequest(reasoningEffort: null));

        handler.LastRequestUri!.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent");
        handler.LastRequestHeaders.TryGetValues("x-goog-api-key", out var apiKeyValues).Should().BeTrue();
        apiKeyValues!.Single().Should().Be("gemini-key");
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().Be("You are helpful.");
        requestJson.RootElement.GetProperty("contents")[0].GetProperty("role").GetString().Should().Be("user");
        requestJson.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString()
            .Should().Be("lookup_weather");

        response.FirstChoice!.Message.GetText().Should().Be("hello from gemini");
        response.FirstChoice!.Message.ToolCalls.Should().HaveCount(1);
        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.Usage!.PromptTokensDetails!.CachedTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task GoogleGemini_StreamCompletionAsync_UsesSseEndpointAndAggregatesText()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            """
            data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hi"}]}}]}

            data: {"candidates":[{"content":{"role":"model","parts":[{"text":" there"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":2,"totalTokenCount":4}}

            """));

        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiChatClient(
            httpClient,
            new GoogleGeminiChatConfig
            {
                ApiKey = "gemini-key"
            },
            "gemini-2.5-pro");

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            CreateTextRequest(reasoningEffort: null),
            chunk => deltas.Add(chunk.FirstChoice?.Delta.Content ?? string.Empty));

        handler.LastRequestUri!.ToString().Should().Contain(":streamGenerateContent?alt=sse");
        response.FirstChoice!.Message.GetText().Should().Be("Hi there");
        response.Usage!.TotalTokens.Should().Be(4);
        deltas.Should().Equal("Hi", " there");
    }

    [TestMethod]
    public async Task Anthropic_GetCompletionAsync_WithThinking_UsesConfiguredBudget()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "id": "msg_123",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "text", "text": "hello from claude" }
              ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 5,
                "output_tokens": 3
              }
            }
            """));

        using var httpClient = new HttpClient(handler);
        var config = new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 4096,
            ThinkingBudgets = new AnthropicThinkingBudgets(Medium: 2048)
        };
        var client = new AnthropicChatClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hello")
            ],
            model: null,
            reasoningEffort: "medium"));

        handler.LastRequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/messages");
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        requestJson.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
        requestJson.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        requestJson.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(2048);
        response.FirstChoice!.Message.GetText().Should().Be("hello from claude");
    }

    [TestMethod]
    public async Task Anthropic_GetCompletionAsync_WithThinking_RequiresConfiguredBudget()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "id": "msg_123",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "text", "text": "hello from claude" }
              ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 5,
                "output_tokens": 3
              }
            }
            """));

        using var httpClient = new HttpClient(handler);
        var config = new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 4096,
            ThinkingBudgets = new AnthropicThinkingBudgets(
                Minimal: null,
                Low: null,
                Medium: null,
                High: null)
        };
        var client = new AnthropicChatClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hello")
            ],
            model: null,
            reasoningEffort: "low"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Thinking budget not configured for low.");
    }

    [TestMethod]
    public async Task Anthropic_GetCompletionAsync_WithThinking_OmitsSamplingTemperatureAndTopP()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """
            {
              "id": "msg_123",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5-20251001",
              "content": [
                { "type": "text", "text": "hello from claude" }
              ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 5,
                "output_tokens": 3
              }
            }
            """));

        using var httpClient = new HttpClient(handler);
        var config = new AnthropicConfig
        {
            ApiKey = "ant-key",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-haiku-4-5-20251001",
            DefaultMaxTokens = 4096,
            ThinkingBudgets = new AnthropicThinkingBudgets(Low: 2048)
        };
        var client = new AnthropicChatClientFactory(new StaticHttpClientFactory(httpClient), config)
            .CreateClient(null, httpClient);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hello")
            ],
            model: null,
            reasoningEffort: "low",
            samplingParameters: new Dictionary<string, double>
            {
                ["temperature"] = 0.7,
                ["top_p"] = 0.8
            }));

        handler.LastRequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/messages");
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        requestJson.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
        requestJson.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        requestJson.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(2048);
        response.FirstChoice!.Message.GetText().Should().Be("hello from claude");
    }

    [TestMethod]
    public void Anthropic_CreateClient_RequiresExplicitConfiguration()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => JsonResponse("{}")));
        var factory = new AnthropicChatClientFactory(new StaticHttpClientFactory(httpClient));

        Action act = () => factory.CreateClient(null, httpClient);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Anthropic configuration is required.");
    }

    [TestMethod]
    public void OpenAiChat_CreateClient_RequiresExplicitConfiguration()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => JsonResponse("{}")));
        var factory = new OpenAiChatClientFactory(new StaticHttpClientFactory(httpClient));

        Action act = () => factory.CreateClient(null, httpClient);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("OpenAI configuration is required.");
    }

    [TestMethod]
    public void OpenAiResponses_CreateClient_RequiresExplicitConfiguration()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => JsonResponse("{}")));
        var factory = new OpenAiResponsesClientFactory(new StaticHttpClientFactory(httpClient));

        Action act = () => factory.CreateClient(null, httpClient);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("OpenAI configuration is required.");
    }

    private static ChatCompletionRequest CreateTextRequest(string? reasoningEffort = "medium") =>
        new(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hello")
            ],
            model: null,
            reasoningEffort: reasoningEffort);

    private static ChatCompletionRequest CreateToolRequest(string? reasoningEffort = "medium") =>
        new(
            messages:
            [
                new ChatMessage(ChatRole.System, "You are helpful."),
                new ChatMessage(ChatRole.User, "What's the weather?")
            ],
            tools:
            [
                new ChatToolDefinition(
                    new ChatFunctionDefinition(
                        "lookup_weather",
                        "Look up weather",
                        JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""")))
            ],
            model: null,
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["temperature"] = 0.2,
                ["top_p"] = 0.9
            },
            reasoningEffort: reasoningEffort);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(string sse) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public Uri? LastRequestUri { get; private set; }
        public AuthenticationHeaderValue? LastAuthorizationHeader => LastRequestHeaders.Authorization;
        public HttpRequestHeadersSnapshot LastRequestHeaders { get; private set; } = new();
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestHeaders = new HttpRequestHeadersSnapshot(request.Headers);
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class HttpRequestHeadersSnapshot
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _headers;

        public HttpRequestHeadersSnapshot()
        {
            _headers = new(StringComparer.OrdinalIgnoreCase);
        }

        public HttpRequestHeadersSnapshot(HttpRequestHeaders headers)
        {
            Authorization = headers.Authorization;
            _headers = headers.ToDictionary(
                header => header.Key,
                header => (IReadOnlyList<string>)header.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        public AuthenticationHeaderValue? Authorization { get; }

        public bool TryGetValues(string name, out IReadOnlyList<string>? values) =>
            _headers.TryGetValue(name, out values);
    }
}
