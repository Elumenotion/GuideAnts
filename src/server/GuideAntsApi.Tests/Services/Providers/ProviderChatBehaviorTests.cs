using System.Net;
using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.HuggingFace;
using AntRunner.Chat.OpenRouter;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Row-owned chat behavior (ThinkingControlJson / RequestFieldsWhenToolsPresentJson) applied by the
/// OpenAI-compatible providers. Without it, Hugging Face can only send <c>reasoning_effort</c> — which
/// most routed providers ignore — and neither client can reach vendor body fields.
/// </summary>
[TestClass]
public sealed class ProviderChatBehaviorTests
{
    private static ProviderChatBehavior EnableThinkingBehavior() => new(
        new ProviderThinkingControl(
            DefaultChoice: "enabled",
            ChoiceActions: new Dictionary<string, IReadOnlyList<ProviderChatBehaviorAction>>(StringComparer.Ordinal)
            {
                ["none"] =
                [
                    new ProviderChatBehaviorAction(
                        ProviderChatBehaviorActionTarget.NestedRequestField,
                        "chat_template_kwargs.enable_thinking",
                        false)
                ],
                ["high"] =
                [
                    new ProviderChatBehaviorAction(
                        ProviderChatBehaviorActionTarget.NestedRequestField,
                        "chat_template_kwargs.enable_thinking",
                        true),
                    new ProviderChatBehaviorAction(
                        ProviderChatBehaviorActionTarget.RequestField,
                        "reasoning_effort",
                        "high")
                ]
            }));

    private static ProviderChatBehavior ExtraFieldsBehavior(string json) => new(
        ThinkingControl: null,
        ExtraRequestFields: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    [TestMethod]
    public async Task HuggingFace_AppliesNestedThinkingControl_AndDropsReasoningEffort()
    {
        var handler = new CapturingHandler(_ => JsonResponse(HuggingFaceTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig { Token = "t" },
            "Qwen/Qwen3-32B",
            logger: null,
            behavior: EnableThinkingBehavior());

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "none"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
            .Should().BeFalse();
        json.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task HuggingFace_ThinkingControlActions_CanSetReasoningEffortThemselves()
    {
        var handler = new CapturingHandler(_ => JsonResponse(HuggingFaceTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig { Token = "t" },
            "Qwen/Qwen3-32B",
            logger: null,
            behavior: EnableThinkingBehavior());

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "high"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
            .Should().BeTrue();
        json.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [TestMethod]
    public async Task HuggingFace_UnconfiguredChoice_KeepsBuiltInReasoningEffort()
    {
        var handler = new CapturingHandler(_ => JsonResponse(HuggingFaceTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig { Token = "t" },
            "Qwen/Qwen3-32B",
            logger: null,
            behavior: EnableThinkingBehavior());

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "medium"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("medium");
        json.RootElement.TryGetProperty("chat_template_kwargs", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task HuggingFace_MergesExtraRequestFields()
    {
        var handler = new CapturingHandler(_ => JsonResponse(HuggingFaceTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig { Token = "t" },
            "m",
            logger: null,
            behavior: ExtraFieldsBehavior("""{"parallel_tool_calls":false,"seed":7}"""));

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("seed").GetInt32().Should().Be(7);
        json.RootElement.GetProperty("model").GetString().Should().Be("m");
    }

    [TestMethod]
    public async Task HuggingFace_WithoutBehavior_KeepsExistingPayloadShape()
    {
        var handler = new CapturingHandler(_ => JsonResponse(HuggingFaceTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(httpClient, new HuggingFaceChatConfig { Token = "t" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "low",
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal) { ["temperature"] = 0.4 }));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("low");
        json.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.4);
        json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("hi");
    }

    [TestMethod]
    public async Task OpenRouter_ThinkingControl_ReplacesBuiltInReasoningMapping()
    {
        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "qwen/qwen3-32b",
            logger: null,
            behavior: EnableThinkingBehavior());

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "none"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        ReadBuiltInReasoning(json.RootElement).Should().BeNull();
        json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task OpenRouter_UnconfiguredChoice_KeepsBuiltInReasoningMapping()
    {
        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "m",
            logger: null,
            behavior: EnableThinkingBehavior());

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "medium"));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        ReadBuiltInReasoning(json.RootElement).Should().Be("medium");
        json.RootElement.TryGetProperty("chat_template_kwargs", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task OpenRouter_MergesExtraRequestFields()
    {
        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        // Primitives only — the row column rejects object values
        // (see ExtraRequestFieldsColumn_RejectsObjectValues).
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "m",
            logger: null,
            behavior: ExtraFieldsBehavior("""{"parallel_tool_calls":false,"seed":11}"""));

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("seed").GetInt32().Should().Be(11);
    }

    [TestMethod]
    public async Task OpenRouter_SystemMessagePrefixAction_PrependsToSystemMessage()
    {
        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var behavior = new ProviderChatBehavior(
            new ProviderThinkingControl(
                DefaultChoice: "none",
                ChoiceActions: new Dictionary<string, IReadOnlyList<ProviderChatBehaviorAction>>(StringComparer.Ordinal)
                {
                    ["none"] =
                    [
                        new ProviderChatBehaviorAction(
                            ProviderChatBehaviorActionTarget.SystemMessagePrefix,
                            "thinking",
                            "/no_think")
                    ]
                }));
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "m",
            logger: null,
            behavior: behavior);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, "You are helpful."),
                new ChatMessage(ChatRole.User, "hi")
            ],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()
            .Should().Be("/no_think\n\nYou are helpful.");
    }

    [TestMethod]
    public async Task OpenRouter_DefaultChoiceApplies_WhenRequestHasNoReasoningEffort()
    {
        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var behavior = new ProviderChatBehavior(
            new ProviderThinkingControl(
                DefaultChoice: "none",
                ChoiceActions: new Dictionary<string, IReadOnlyList<ProviderChatBehaviorAction>>(StringComparer.Ordinal)
                {
                    ["none"] =
                    [
                        new ProviderChatBehaviorAction(
                            ProviderChatBehaviorActionTarget.NestedRequestField,
                            "chat_template_kwargs.enable_thinking",
                            false)
                    ]
                }));
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "m",
            logger: null,
            behavior: behavior);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task OpenRouter_WithoutBehavior_KeepsExistingPayloadShape()
    {
        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new OpenRouterChatConfig { ApiKey = "k" }, "m");

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "high",
            samplingParameters: new Dictionary<string, double>(StringComparer.Ordinal) { ["top_k"] = 30 }));

        using var json = JsonDocument.Parse(handler.LastRequestBody);
        ReadBuiltInReasoning(json.RootElement).Should().Be("high");
        json.RootElement.GetProperty("top_k").GetDouble().Should().Be(30);
        json.RootElement.TryGetProperty("chat_template_kwargs", out _).Should().BeFalse();
    }

    /// <summary>
    /// End to end from the catalog row's JSON columns: row strings -> RuntimeProfileData ->
    /// ProviderChatBehavior -> request body, using the minimal operator config — override only the
    /// no-effort case (where the built-in mapping sends nothing and the model thinks by default) and
    /// let every explicit effort fall through to <c>ResolveReasoning</c>. Also covers an object-valued
    /// <c>RequestField</c>, which the thinking-control column accepts though extra fields do not.
    /// </summary>
    [TestMethod]
    public async Task RowDefaultChoiceOverridesNoEffort_ExplicitEffortsFallThroughToBuiltInMapping()
    {
        const string thinkingControlJson = """
            {
              "defaultChoice": "none",
              "choiceActions": {
                "none": [{ "target": "RequestField", "key": "reasoning", "value": { "enabled": false } }]
              }
            }
            """;
        var profile = GuideAntsApi.Services.LlamaCpp.RuntimeProfileDataJson.FromJsonStrings(
            "deepseek/deepseek-v4-flash:nitro",
            combineSystemAndDeveloperMessages: true,
            thoughtBlockPattern: null,
            samplingParametersJson: "{}",
            thinkingControlJson: thinkingControlJson,
            requestFieldsWhenToolsPresentJson: """{"parallel_tool_calls":false}""");
        var behavior = GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory
            .ToProviderChatBehavior(profile);
        behavior.Should().NotBeNull();

        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "deepseek/deepseek-v4-flash:nitro",
            logger: null,
            behavior: behavior);

        // No reasoning effort on the request: the row's defaultChoice must still disable thinking.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null));

        using (var json = JsonDocument.Parse(handler.LastRequestBody))
        {
            json.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean().Should().BeFalse();
            json.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
            json.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        }

        // An explicit effort the row does not configure keeps the client's built-in mapping, so the
        // row never has to restate low/medium/high.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "high"));

        using (var json = JsonDocument.Parse(handler.LastRequestBody))
        {
            ReadBuiltInReasoning(json.RootElement).Should().Be("high");
            json.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
        }
    }

    /// <summary>
    /// Pins the documented operator config for a DeepSeek-style OpenRouter row: every choice mapped
    /// explicitly, so the row is self-sufficient and produces the same wire bytes regardless of what
    /// the client's own reasoning mapping would have done.
    /// </summary>
    [TestMethod]
    public async Task DeepSeekRowConfig_MapsEveryChoiceWithoutRelyingOnFallback()
    {
        const string thinkingControlJson = """
            {
              "defaultChoice": "none",
              "choiceActions": {
                "none":   [{ "target": "RequestField", "key": "reasoning", "value": { "enabled": false } }],
                "low":    [{ "target": "RequestField", "key": "reasoning", "value": { "effort": "low" } }],
                "medium": [{ "target": "RequestField", "key": "reasoning", "value": { "effort": "medium" } }],
                "high":   [{ "target": "RequestField", "key": "reasoning", "value": { "effort": "high" } }]
              }
            }
            """;
        var behavior = GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory.ToProviderChatBehavior(
            GuideAntsApi.Services.LlamaCpp.RuntimeProfileDataJson.FromJsonStrings(
                "deepseek/deepseek-v4-flash:nitro",
                combineSystemAndDeveloperMessages: true,
                thoughtBlockPattern: null,
                samplingParametersJson: "{}",
                thinkingControlJson: thinkingControlJson,
                requestFieldsWhenToolsPresentJson: """{"parallel_tool_calls":false}"""));

        var handler = new CapturingHandler(_ => JsonResponse(OpenRouterTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new OpenRouterChatConfig { ApiKey = "k" },
            "deepseek/deepseek-v4-flash:nitro",
            logger: null,
            behavior: behavior);

        // The phone-call case: no effort selected anywhere.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null));
        using (var json = JsonDocument.Parse(handler.LastRequestBody))
        {
            json.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean().Should().BeFalse();
            json.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
            json.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        }

        foreach (var effort in new[] { "low", "medium", "high" })
        {
            await client.GetCompletionAsync(new ChatCompletionRequest(
                messages: [new ChatMessage(ChatRole.User, "hi")],
                model: null,
                reasoningEffort: effort));
            using var json = JsonDocument.Parse(handler.LastRequestBody);
            json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be(effort);
            json.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// Same row-columns path for Hugging Face, where thinking control is the only mechanism that
    /// exists: the built-in mapping is a bare <c>reasoning_effort</c> the router's providers ignore,
    /// so <c>chat_template_kwargs.enable_thinking</c> is reachable only as a nested action.
    /// </summary>
    [TestMethod]
    public async Task HuggingFaceRowConfig_TogglesEnableThinkingPerChoice()
    {
        const string thinkingControlJson = """
            {
              "defaultChoice": "none",
              "choiceActions": {
                "none":    [{ "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": false }],
                "enabled": [{ "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": true }]
              }
            }
            """;
        var behavior = GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory.ToProviderChatBehavior(
            GuideAntsApi.Services.LlamaCpp.RuntimeProfileDataJson.FromJsonStrings(
                "Qwen/Qwen3-32B",
                combineSystemAndDeveloperMessages: true,
                thoughtBlockPattern: null,
                samplingParametersJson: "{}",
                thinkingControlJson: thinkingControlJson,
                requestFieldsWhenToolsPresentJson: "{}"));

        var handler = new CapturingHandler(_ => JsonResponse(HuggingFaceTextResponse("hi")));
        using var httpClient = new HttpClient(handler);
        var client = new HuggingFaceChatClient(
            httpClient,
            new HuggingFaceChatConfig { Token = "t" },
            "Qwen/Qwen3-32B",
            logger: null,
            behavior: behavior);

        // No effort selected: the row's defaultChoice disables thinking.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null));
        using (var json = JsonDocument.Parse(handler.LastRequestBody))
        {
            json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
                .Should().BeFalse();
            json.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        }

        // A guide selecting "enabled" must reach the wire as the opposite kwarg.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: null,
            reasoningEffort: "enabled"));
        using (var json = JsonDocument.Parse(handler.LastRequestBody))
        {
            json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()
                .Should().BeTrue();
            json.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// The extra-request-fields column rejects object values (llama-era
    /// <see cref="GuideAntsApi.Services.LlamaCpp.RuntimeProfileRequestFieldsValidator"/> rule), and the
    /// non-local resolver treats a failed parse as "no behavior" — so an object there silently drops the
    /// row's thinking control too. Documents the trap; see the note in the catalog editor help text.
    /// </summary>
    [TestMethod]
    public void ExtraRequestFieldsColumn_RejectsObjectValues()
    {
        var act = () => GuideAntsApi.Services.LlamaCpp.RuntimeProfileDataJson.FromJsonStrings(
            "openrouter-row",
            combineSystemAndDeveloperMessages: true,
            thoughtBlockPattern: null,
            samplingParametersJson: "{}",
            thinkingControlJson: "{}",
            requestFieldsWhenToolsPresentJson: """{"reasoning":{"enabled":false}}""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*primitive*");
    }

    /// <summary>
    /// Reads whichever shape the client's own reasoning mapping produced. OpenRouter's built-in
    /// mapping is a bare <c>reasoning_effort</c> string on this base and the richer <c>reasoning</c>
    /// object once "Make OpenRouter reasoning controllable from the guide" lands. These tests assert
    /// only that row-owned behavior left that mapping untouched — its exact shape is that change's
    /// contract, covered by <c>OpenRouterChatClientDeepTests</c>.
    /// </summary>
    private static string? ReadBuiltInReasoning(JsonElement root)
    {
        if (root.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
        {
            if (reasoning.TryGetProperty("effort", out var effort))
            {
                return effort.GetString();
            }

            if (reasoning.TryGetProperty("enabled", out var enabled))
            {
                return enabled.GetBoolean() ? "enabled" : "none";
            }
        }

        return root.TryGetProperty("reasoning_effort", out var bare) ? bare.GetString() : null;
    }

    private static string OpenRouterTextResponse(string text) =>
        $$"""{ "choices": [ { "message": { "content": "{{text}}" }, "finish_reason": "stop" } ] }""";

    private static string HuggingFaceTextResponse(string text) =>
        $$"""{ "choices": [ { "message": { "content": "{{text}}" }, "finish_reason": "stop" } ] }""";

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
