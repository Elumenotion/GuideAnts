using System.Net;
using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class LlamaCppChatClientDeepTests
{
    [TestMethod]
    public async Task GetCompletionAsync_BuildsEndpointAndAuthorizationHeader()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(TextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "http://localhost:8000/", ApiKey = "secret", TimeoutSeconds = 300 },
            "qwen3.5-27b",
            QwenProfile());

        await client.GetCompletionAsync(Request());

        handler.LastRequestUri!.ToString().Should().Be("http://localhost:8000/v1/chat/completions");
        handler.LastAuthorization.Should().Be("Bearer secret");
    }

    [TestMethod]
    public async Task GetCompletionAsync_OmitsAuthorization_WhenApiKeyMissing()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(TextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "", TimeoutSeconds = 300 },
            "qwen3.5-27b",
            QwenProfile());

        await client.GetCompletionAsync(Request());

        handler.LastAuthorization.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenBaseUrlMissing()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(TextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "", ApiKey = "k", TimeoutSeconds = 300 },
            "qwen3.5-27b",
            QwenProfile());

        var act = () => client.GetCompletionAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("LlamaCpp BaseUrl is not configured.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenModelMissing()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(TextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "k", TimeoutSeconds = 300 },
            deploymentId: null,
            QwenProfile());

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")], model: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LlamaCpp model is required. Provide an explicit model deployment id.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MergesSamplingDefaults_OverriddenByRequest()
    {
        string? body = null;
        var handler = new LlamaCapturingHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return JsonResponse(TextResponse("ok"));
        });
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "k", TimeoutSeconds = 300 },
            "qwen3.5-27b",
            QwenProfile());

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b",
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal) { ["temperature"] = 0.1 }));

        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.1);
        doc.RootElement.GetProperty("top_p").GetDouble().Should().Be(0.8);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenReasoningChoiceUndefinedInProfile()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(TextResponse("ok")));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "k", TimeoutSeconds = 300 },
            "qwen3.5-27b",
            QwenProfile());

        var act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b",
            reasoningEffort: "ultra"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Reasoning choice 'ultra' is not defined in the profile.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_EnabledThinking_DoesNotEmitDisableFields()
    {
        // Exercises the SystemMessagePrefix thinking-control branch for the "enabled" choice.
        string? body = null;
        var handler = new LlamaCapturingHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return JsonResponse(TextResponse("ok"));
        });
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(
            httpClient,
            new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "k", TimeoutSeconds = 300 },
            "gemma-4",
            GemmaProfile());

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "gemma-4",
            reasoningEffort: "enabled"));

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        // The "enabled" Gemma policy only prepends a system prefix; it must NOT set the
        // disable fields that the "none" policy uses.
        root.TryGetProperty("reasoning_format", out _).Should().BeFalse();
        root.TryGetProperty("chat_template_kwargs", out _).Should().BeFalse();
        root.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        response.FirstChoice!.Message.GetText().Should().Be("ok");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsUsageWithCachedTokens()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [ { "message": { "role": "assistant", "content": "ok" }, "finish_reason": "stop" } ],
              "usage": { "prompt_tokens": 9, "completion_tokens": 4, "total_tokens": 13, "prompt_tokens_details": { "cached_tokens": 2 } }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var response = await client.GetCompletionAsync(Request());

        response.Usage!.PromptTokens.Should().Be(9);
        response.Usage.PromptTokensDetails!.CachedTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenChoicesMissing()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse("""{"usage":{"prompt_tokens":1}}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var act = () => client.GetCompletionAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("llama.cpp response did not include choices.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThrowsWhenMessageMissing()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse("""{"choices":[{"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var act = () => client.GetCompletionAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("llama.cpp response did not include message.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_DefaultsFinishReason_ToToolCalls_WhenToolCallsPresent()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [ { "message": { "role": "assistant", "content": "", "tool_calls": [ { "id": "c1", "function": { "name": "f", "arguments": "{}" } } ] } } ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.ToolCalls.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ParsesToolArguments_FromInvalidStringAsEmptyObject()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [ { "finish_reason": "tool_calls", "message": { "role": "assistant", "content": "", "tool_calls": [ { "id": "c1", "function": { "name": "f", "arguments": "not-json" } } ] } } ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.ToolCalls![0].Function.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.EnumerateObject().Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ParsesToolArguments_FromObject()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [ { "finish_reason": "tool_calls", "message": { "role": "assistant", "content": "", "tool_calls": [ { "id": "c1", "function": { "name": "f", "arguments": { "x": 5 } } } ] } } ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.ToolCalls![0].Function.Arguments.GetProperty("x").GetInt32().Should().Be(5);
    }

    [TestMethod]
    public async Task GetCompletionAsync_ExtractsTextFromArrayContent()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(
            """
            {
              "choices": [ { "finish_reason": "stop", "message": { "role": "assistant", "content": [ { "type": "text", "text": "alpha" }, { "type": "text", "text": "beta" } ] } } ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.GetText().Should().Be("alphabeta");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ReturnsNullUsage_WhenAbsent()
    {
        var handler = new LlamaCapturingHandler(_ => JsonResponse(
            """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"hi"}}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var response = await client.GetCompletionAsync(Request());

        response.Usage.Should().BeNull();
    }

    [TestMethod]
    public async Task StreamCompletionAsync_Classifies503_AsCrash()
    {
        var handler = new LlamaCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("service unavailable", Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var act = () => client.StreamCompletionAsync(Request(), _ => { });

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.Crashed);
        ex.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonStreamOom_ClassifiedAsOutOfMemory()
    {
        var handler = new LlamaCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"cudaMalloc failed: out of memory\"}}", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler);

        var act = () => client.GetCompletionAsync(Request());

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.OutOfMemory);
    }

    private static ChatCompletionRequest Request() =>
        new(messages: [new ChatMessage(ChatRole.User, "hi")], model: "qwen3.5-27b");

    private static LlamaCppChatClient Client(LlamaCapturingHandler handler) =>
        new(
            new HttpClient(handler),
            new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "k", TimeoutSeconds = 300 },
            "qwen3.5-27b",
            QwenProfile());

    private static LlamaCppRuntimeProfileData QwenProfile() =>
        new(
            "qwen3_5",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: @"<think>[\s\S]*?</think>",
            SamplingDefaults: new Dictionary<string, double> { ["temperature"] = 0.7, ["top_p"] = 0.8 },
            ThinkingControl: new ThinkingControl(
                "enabled",
                new Dictionary<string, IReadOnlyList<ThinkingAction>>
                {
                    ["none"] = new List<ThinkingAction>
                    {
                        new(ThinkingActionTarget.NestedRequestField, "chat_template_kwargs.enable_thinking", false),
                        new(ThinkingActionTarget.RequestField, "reasoning_format", "none")
                    },
                    ["enabled"] = new List<ThinkingAction>
                    {
                        new(ThinkingActionTarget.NestedRequestField, "chat_template_kwargs.enable_thinking", true)
                    }
                }),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>
            {
                ["parallel_tool_calls"] = JsonSerializer.SerializeToElement(true)
            });

    private static LlamaCppRuntimeProfileData GemmaProfile() =>
        new(
            "gemma4",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingDefaults: new Dictionary<string, double> { ["temperature"] = 0.8, ["top_p"] = 0.9 },
            ThinkingControl: new ThinkingControl(
                "enabled",
                new Dictionary<string, IReadOnlyList<ThinkingAction>>
                {
                    ["none"] = new List<ThinkingAction>
                    {
                        new(ThinkingActionTarget.RequestField, "reasoning_format", "none")
                    },
                    ["enabled"] = new List<ThinkingAction>
                    {
                        new(ThinkingActionTarget.SystemMessagePrefix, "", "<|think|>\n")
                    }
                }),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>
            {
                ["parallel_tool_calls"] = JsonSerializer.SerializeToElement(true)
            });

    private static string TextResponse(string text) =>
        $$"""{"choices":[{"message":{"role":"assistant","content":"{{text}}"},"finish_reason":"stop"}]}""";

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class LlamaCapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public LlamaCapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(req => Task.FromResult(responder(req))) { }

        public LlamaCapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return await _responder(request);
        }
    }
}
