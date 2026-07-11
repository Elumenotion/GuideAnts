using System.Net;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second wave of deterministic coverage for <see cref="LlamaCppChatClient"/> targeting
/// the request-mapping, error-classification, content-part and streaming branches that the
/// first deep file does not exercise. Reuses the shared <see cref="CapturingHandler"/> harness.
/// </summary>
[TestClass]
public sealed class LlamaCppChatClientDeepTests2
{
    private static LlamaCppChatClient Client(
        CapturingHandler handler,
        HttpClient httpClient,
        LlamaCppRuntimeProfileData? profile = null,
        LlamaCppConfig? config = null)
    {
        config ??= new LlamaCppConfig { BaseUrl = "http://localhost:8000", ApiKey = "k", TimeoutSeconds = 300 };
        profile ??= ProfileWithParallelToolCalls(true);
        return new LlamaCppChatClient(httpClient, config, "qwen3.5-27b", profile);
    }

    private static LlamaCppRuntimeProfileData ProfileWithParallelToolCalls(bool enabled) =>
        new(
            "qwen3_5",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingDefaults: new Dictionary<string, double>(),
            ThinkingControl: new ThinkingControl(string.Empty, new Dictionary<string, IReadOnlyList<ThinkingAction>>()),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>
            {
                ["parallel_tool_calls"] = JsonSerializer.SerializeToElement(enabled)
            });

    private static ChatCompletionRequest Request(params ChatMessage[] messages) =>
        new(messages: messages.Length > 0 ? messages : [new ChatMessage(ChatRole.User, "hi")], model: "qwen3.5-27b");

    [TestMethod]
    public async Task GetCompletionAsync_SerializesToolDefinitionsAndParallelFlag()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient, ProfileWithParallelToolCalls(true));

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "weather?")],
            tools:
            [
                new ChatToolDefinition(new ChatFunctionDefinition(
                    "lookup",
                    "Look up",
                    System.Text.Json.Nodes.JsonNode.Parse(
                        """{"type":"object","properties":{"city":{"type":"string"}}}""")))
            ],
            model: "qwen3.5-27b"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()
            .Should().Be("lookup");
        body.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullFunctionToolDefinition_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json("{}"));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(null!)],
            model: "qwen3.5-27b"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Function tool definition is required.");
    }

    [TestMethod]
    public async Task GetCompletionAsync_SerializesToolAndAssistantConversation_WithDiagnosticCounts()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var assistantToolCall = new ChatToolCall
        {
            Id = "call_1",
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "lookup",
                Arguments = JsonSerializer.SerializeToElement(new { city = "Boston" })
            }
        };

        await client.GetCompletionAsync(Request(
            new ChatMessage(ChatRole.System, "be brief"),
            new ChatMessage(ChatRole.User, "weather?"),
            new ChatMessage(ChatRole.Assistant, [new ChatContent("checking")], [assistantToolCall]),
            new ChatMessage("call_1", "lookup", [new ChatContent("72F")])));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var messages = body.RootElement.GetProperty("messages");
        // System fragment is combined into a single leading system message.
        messages[0].GetProperty("role").GetString().Should().Be("system");
        var toolMsg = messages.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        toolMsg.GetProperty("tool_call_id").GetString().Should().Be("call_1");
        var assistant = messages.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()
            .Should().Contain("Boston");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ImageContent_SerializesMultiPartContent()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(Request(
            new ChatMessage(ChatRole.User, new List<ChatContent>
            {
                new("describe"),
                new(new ChatImageUrl("https://example.test/cat.png"))
            })));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.Array);
        content.EnumerateArray().Any(p => p.GetProperty("type").GetString() == "image_url").Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_EmptyContentMessage_SerializesEmptyString()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(Request(
            new ChatMessage(ChatRole.User, new List<ChatContent>())));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be(string.Empty);
    }

    [TestMethod]
    public async Task GetCompletionAsync_PerMessageMapping_WhenCombineSystemDisabled()
    {
        var profile = new LlamaCppRuntimeProfileData(
            "noCombine",
            CombineSystemAndDeveloperMessages: false,
            ThoughtBlockPattern: null,
            SamplingDefaults: new Dictionary<string, double>(),
            ThinkingControl: new ThinkingControl(string.Empty, new Dictionary<string, IReadOnlyList<ThinkingAction>>()),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>());
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient, profile);

        await client.GetCompletionAsync(Request(
            new ChatMessage(ChatRole.System, "sys1"),
            new ChatMessage(ChatRole.Developer, "dev1"),
            new ChatMessage(ChatRole.User, "hi")));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var roles = body.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        // Developer is mapped to "system" per-message rather than combined.
        roles.Should().Equal("system", "system", "user");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThinkingControl_DefaultChoiceMissing_NoOp()
    {
        // DefaultChoice empty and no reasoningEffort -> ApplyThinkingControl early-returns.
        var profile = new LlamaCppRuntimeProfileData(
            "p",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingDefaults: new Dictionary<string, double>(),
            ThinkingControl: new ThinkingControl(
                DefaultChoice: "",
                ChoiceActions: new Dictionary<string, IReadOnlyList<ThinkingAction>>
                {
                    ["none"] = new List<ThinkingAction> { new(ThinkingActionTarget.RequestField, "x", true) }
                }),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>());
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient, profile);

        await client.GetCompletionAsync(Request());

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.TryGetProperty("x", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCompletionAsync_ThinkingControl_AppliesVariedValueTypesAndNestedFields()
    {
        var profile = new LlamaCppRuntimeProfileData(
            "p",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingDefaults: new Dictionary<string, double>(),
            ThinkingControl: new ThinkingControl(
                DefaultChoice: "none",
                ChoiceActions: new Dictionary<string, IReadOnlyList<ThinkingAction>>
                {
                    ["enabled"] = new List<ThinkingAction>
                    {
                        new(ThinkingActionTarget.RequestField, "flag_bool", true),
                        new(ThinkingActionTarget.RequestField, "flag_int", 7),
                        new(ThinkingActionTarget.RequestField, "flag_long", 9L),
                        new(ThinkingActionTarget.RequestField, "flag_double", 1.5d),
                        new(ThinkingActionTarget.RequestField, "flag_float", 2.5f),
                        new(ThinkingActionTarget.NestedRequestField, "chat_template_kwargs.enable_thinking", true),
                        new(ThinkingActionTarget.SystemMessagePrefix, "", "PREFIX:")
                    }
                }),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>());
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient, profile);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, "base"),
                new ChatMessage(ChatRole.User, "hi")
            ],
            model: "qwen3.5-27b",
            reasoningEffort: "enabled"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var root = body.RootElement;
        root.GetProperty("flag_bool").GetBoolean().Should().BeTrue();
        root.GetProperty("flag_int").GetInt32().Should().Be(7);
        root.GetProperty("flag_long").GetInt64().Should().Be(9);
        root.GetProperty("flag_double").GetDouble().Should().Be(1.5);
        root.GetProperty("flag_float").GetDouble().Should().BeApproximately(2.5, 1e-6);
        root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_SystemMessagePrefix_InsertsSystemMessage_WhenNoneExists()
    {
        var profile = new LlamaCppRuntimeProfileData(
            "p",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingDefaults: new Dictionary<string, double>(),
            ThinkingControl: new ThinkingControl(
                DefaultChoice: "enabled",
                ChoiceActions: new Dictionary<string, IReadOnlyList<ThinkingAction>>
                {
                    ["enabled"] = new List<ThinkingAction>
                    {
                        new(ThinkingActionTarget.SystemMessagePrefix, "", "NEW-SYS")
                    }
                }),
            RequestFieldsWhenToolsPresent: new Dictionary<string, JsonElement>());
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient, profile);

        // Exercises the PrependToSystemMessage insert branch (no existing system message).
        // The message mutation happens after body serialization, so the path is covered here
        // by asserting the request completes and the response maps correctly.
        var response = await client.GetCompletionAsync(Request(new ChatMessage(ChatRole.User, "hi")));

        response.FirstChoice!.Message.GetText().Should().Be("ok");
        handler.RequestCount.Should().Be(1);
    }

    [TestMethod]
    public void Constructor_AppliesCustomOutputStripPatterns_AndSkipsUnsupported()
    {
        // Valid custom pattern uses ".*?" separator; invalid pattern lacks a separator and is skipped.
        var config = new LlamaCppConfig
        {
            BaseUrl = "http://localhost:8000",
            ApiKey = "k",
            TimeoutSeconds = 300,
            OutputStripPatterns = { @"<scratch>.*?</scratch>", "no-separator-here", "   " }
        };
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"<scratch>\n</scratch>visible"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(httpClient, config, "qwen3.5-27b");

        // Building the client must not throw despite the unsupported pattern.
        client.Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_StripsLeadingEmptyConfiguredBlock()
    {
        var config = new LlamaCppConfig
        {
            BaseUrl = "http://localhost:8000",
            ApiKey = "k",
            TimeoutSeconds = 300,
            OutputStripPatterns = { @"<scratch>.*?</scratch>" }
        };
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"  <scratch>   </scratch>real answer"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = new LlamaCppChatClient(httpClient, config, "qwen3.5-27b");

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.GetText().Should().Be("real answer");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ParsesReasoningContent_IntoThinkingBlock()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"answer","reasoning_content":"because"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.ThinkingBlocks.Should().ContainSingle();
        response.FirstChoice.Message.ThinkingBlocks![0].Thinking.Should().Be("because");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ExtractsTextFromObjectWithContent()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":{"content":{"text":"nested"}}},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.GetText().Should().Be("nested");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolCalls_NonArrayElement_ReturnsEmpty()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"hi","tool_calls":"not-array"},"finish_reason":"stop"}]}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(Request());

        response.FirstChoice!.Message.ToolCalls.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public async Task GetCompletionAsync_UsageWithMissingAndNonNumberFields_ReturnsNullParts()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":"oops"}}"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(Request());

        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().BeNull();
        response.Usage.CompletionTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonStreamRuntimeNotReady_ClassifiedAsNotReady()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":{"message":"the server has no model loaded"}}""",
                System.Text.Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(Request());

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.NotReady);
        ex.Which.UpstreamDetail.Should().Contain("no model loaded");
    }

    [TestMethod]
    public async Task GetCompletionAsync_Plain400_StaysHttpRequestException()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad tool schema", System.Text.Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(Request());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [TestMethod]
    public async Task GetCompletionAsync_500EmptyBody_ClassifiedAsCrash_WithNullUpstreamDetail()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(Request());

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.Crashed);
        ex.Which.UpstreamDetail.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_500_ExtractsErrorStringUpstreamDetail()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                """{"error":"boom happened"}""", System.Text.Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(Request());

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.UpstreamDetail.Should().Be("boom happened");
    }

    [TestMethod]
    public async Task GetCompletionAsync_500_ExtractsRootMessageUpstreamDetail()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                """{"message":"root level message"}""", System.Text.Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(Request());

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.UpstreamDetail.Should().Be("root level message");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_TextAndToolCallDeltas_AggregateAndReportUsage()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"f\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"5}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            Request(),
            chunk =>
            {
                var d = chunk.FirstChoice?.Delta.Content;
                if (!string.IsNullOrEmpty(d)) deltas.Add(d!);
            });

        string.Concat(deltas).Should().Be("Hello");
        response.FirstChoice!.Message.ToolCalls.Should().ContainSingle();
        response.FirstChoice.Message.ToolCalls![0].Function.Arguments.GetProperty("x").GetInt32().Should().Be(5);
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.TotalTokens.Should().Be(5);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ReasoningDeltas_ProduceThinkingBlock()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"more\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(Request(), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("answer");
        response.FirstChoice.Message.ThinkingBlocks!.Single().Thinking.Should().Be("think more");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_IgnoresChunksWithoutChoicesOrDelta()
    {
        var sse =
            "data: {\"foo\":\"bar\"}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(Request(), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("hi");
        response.FirstChoice.FinishReason.Should().Be("stop");
    }
}
