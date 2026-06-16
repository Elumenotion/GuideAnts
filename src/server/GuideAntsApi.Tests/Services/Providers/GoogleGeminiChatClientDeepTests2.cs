using System.Net;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.GoogleGemini;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Second-wave coverage for <see cref="GoogleGeminiChatClient"/>: thinking-config mapping for the
/// 2.5 and 3+ model families, image/data-URL parts, tool declaration/response handling, finish-reason
/// normalization, candidate-content guards and the streaming function-call accumulator.
/// </summary>
[TestClass]
public sealed class GoogleGeminiChatClientDeepTests2
{
    private static GoogleGeminiChatClient Client(CapturingHandler handler, HttpClient httpClient, string? model = "gemini-2.5-flash") =>
        new(httpClient, new GoogleGeminiChatConfig { ApiKey = "g-key" }, model);

    private static ChatCompletionRequest UserRequest(string? model = null, string? reasoningEffort = null) =>
        new(messages: [new ChatMessage(ChatRole.User, "hi")], model: model, reasoningEffort: reasoningEffort);

    private static string CandidateJson(string text = "ok", string finishReason = "STOP") =>
        $$"""
        {
          "candidates": [
            { "content": { "role": "model", "parts": [ { "text": "{{text}}" } ] }, "finishReason": "{{finishReason}}" }
          ],
          "usageMetadata": { "promptTokenCount": 3, "candidatesTokenCount": 2, "totalTokenCount": 5 }
        }
        """;

    [TestMethod]
    [DataRow("low", 2048)]
    [DataRow("medium", 8192)]
    [DataRow("high", 24576)]
    public async Task GetCompletionAsync_Gemini25_MapsReasoningEffortToThinkingBudget(string effort, int budget)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(UserRequest("gemini-2.5-pro", effort));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig")
            .GetProperty("thinkingBudget").GetInt32().Should().Be(budget);
    }

    [TestMethod]
    [DataRow("minimal")]
    [DataRow("low")]
    [DataRow("medium")]
    [DataRow("high")]
    public async Task GetCompletionAsync_Gemini3_MapsReasoningEffortToThinkingLevel(string level)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(UserRequest("gemini-3-pro-preview", level));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig")
            .GetProperty("thinkingLevel").GetString().Should().Be(level);
    }

    [TestMethod]
    public async Task GetCompletionAsync_Gemini25_UnsupportedEffort_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest("gemini-2.5-flash", "ultra"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reasoning_effort*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_Gemini3_UnsupportedEffort_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest("gemini-3-pro", "ultra"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reasoning_effort*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_NullFunctionTool_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(null!)],
            model: "gemini-2.5-flash"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*require a function definition*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_ToolMessageWithJsonAndPlainText_SerializesFunctionResponse()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "hi"),
                new ChatMessage("call_1", "lookup", [new ChatContent("""{"temp":72}""")]),
                new ChatMessage("call_2", "echo", [new ChatContent("plain text not json")])
            ],
            model: "gemini-2.5-flash"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var contents = body.RootElement.GetProperty("contents");
        var responses = contents.EnumerateArray()
            .SelectMany(c => c.GetProperty("parts").EnumerateArray())
            .Where(p => p.TryGetProperty("functionResponse", out _))
            .ToList();
        responses.Should().HaveCount(2);
        responses[0].GetProperty("functionResponse").GetProperty("response").GetProperty("temp").GetInt32().Should().Be(72);
        responses[1].GetProperty("functionResponse").GetProperty("response").GetProperty("content").GetString()
            .Should().Be("plain text not json");
    }

    [TestMethod]
    public async Task GetCompletionAsync_AssistantToolCallWithNullArguments_OmitsArgs()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var toolCall = new ChatToolCall
        {
            Id = "c1",
            Type = "function",
            Function = new ChatToolCallFunction { Name = "noargs", Arguments = default }
        };

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "hi"),
                new ChatMessage(ChatRole.Assistant, new List<ChatContent> { new("calling") }, [toolCall])
            ],
            model: "gemini-2.5-flash"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var functionCall = body.RootElement.GetProperty("contents").EnumerateArray()
            .SelectMany(c => c.GetProperty("parts").EnumerateArray())
            .Single(p => p.TryGetProperty("functionCall", out _))
            .GetProperty("functionCall");
        functionCall.GetProperty("name").GetString().Should().Be("noargs");
        functionCall.TryGetProperty("args", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCompletionAsync_Base64ImageData_SerializesInlineData()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, new List<ChatContent>
                {
                    new(new ChatImageUrl("data:image/png;base64,QUJD"))
                })
            ],
            model: "gemini-2.5-flash"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var part = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        part.GetProperty("inlineData").GetProperty("mimeType").GetString().Should().Be("image/png");
    }

    [TestMethod]
    [DataRow("https://example.test/cat.png", "image/png")]
    [DataRow("https://example.test/cat.jpg", "image/jpeg")]
    [DataRow("https://example.test/cat.webp", "image/webp")]
    [DataRow("https://example.test/cat.gif", "image/gif")]
    [DataRow("https://example.test/cat.bin", "application/octet-stream")]
    public async Task GetCompletionAsync_RemoteImageUrl_GuessesMimeTypeFileData(string url, string expectedMime)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, new List<ChatContent> { new(new ChatImageUrl(url)) })],
            model: "gemini-2.5-flash"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        var fileData = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("fileData");
        fileData.GetProperty("mimeType").GetString().Should().Be(expectedMime);
        fileData.GetProperty("fileUri").GetString().Should().Be(url);
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonBase64DataUrl_FallsBackToFileData()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        // No ";base64" suffix -> not parsed as inline data, treated as file data.
        await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, new List<ChatContent> { new(new ChatImageUrl("data:image/png,raw")) })],
            model: "gemini-2.5-flash"));

        using var body = JsonDocument.Parse(handler.LastRequestBody);
        body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0]
            .TryGetProperty("fileData", out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_NoNonSystemMessage_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.System, "system only")],
            model: "gemini-2.5-flash"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one non-system message*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_SystemNonTextContent_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson()));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.System, new List<ChatContent> { new(new ChatImageUrl("https://x/y.png")) }),
                new ChatMessage(ChatRole.User, "hi")
            ],
            model: "gemini-2.5-flash"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only support text*");
    }

    [TestMethod]
    [DataRow("MAX_TOKENS", "length")]
    [DataRow("SAFETY", "stop")]
    [DataRow("RECITATION", "stop")]
    [DataRow("TOO_MANY_TOOL_CALLS", "tool_calls")]
    [DataRow("UNEXPECTED_TOOL_CALL", "tool_calls")]
    [DataRow("WEIRD", "stop")]
    public async Task GetCompletionAsync_NormalizesFinishReasons(string upstream, string expected)
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson(finishReason: upstream)));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest("gemini-2.5-flash"));

        response.FirstChoice!.FinishReason.Should().Be(expected);
    }

    [TestMethod]
    public async Task GetCompletionAsync_UnspecifiedFinishReason_MapsToNull()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(CandidateJson(finishReason: "FINISH_REASON_UNSPECIFIED")));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest("gemini-2.5-flash"));

        response.FirstChoice!.FinishReason.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCompletionAsync_CandidateWithoutContent_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """{ "candidates": [ { "finishReason": "STOP" } ] }"""));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest("gemini-2.5-flash"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*candidate content is required*");
    }

    [TestMethod]
    public async Task GetCompletionAsync_FunctionCallResponse_MapsToolCallAndCachedUsage()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json(
            """
            {
              "candidates": [
                {
                  "content": { "role": "model", "parts": [ { "functionCall": { "name": "lookup", "args": { "city": "Rome" } } } ] },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": { "promptTokenCount": 4, "candidatesTokenCount": 3, "totalTokenCount": 7, "cachedContentTokenCount": 2 }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.GetCompletionAsync(UserRequest("gemini-2.5-flash"));

        var toolCall = response.FirstChoice!.Message.ToolCalls!.Single();
        toolCall.Function.Name.Should().Be("lookup");
        toolCall.Function.Arguments.GetProperty("city").GetString().Should().Be("Rome");
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.PromptTokensDetails!.CachedTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_TextAndFunctionCall_Aggregate()
    {
        var sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Hel\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"lo\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"f\",\"args\":{\"n\":1}}}]},\"finishReason\":\"STOP\"}]}\n\n" +
            "data: {\"usageMetadata\":{\"promptTokenCount\":2,\"candidatesTokenCount\":2,\"totalTokenCount\":4}}\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var deltas = new List<string>();
        var response = await client.StreamCompletionAsync(
            UserRequest("gemini-2.5-flash"),
            chunk =>
            {
                var d = chunk.FirstChoice?.Delta.Content;
                if (!string.IsNullOrEmpty(d)) deltas.Add(d!);
            });

        string.Concat(deltas).Should().Be("Hello");
        response.FirstChoice!.Message.GetText().Should().Be("Hello");
        response.FirstChoice.Message.ToolCalls!.Single().Function.Name.Should().Be("f");
        response.FirstChoice.FinishReason.Should().Be("tool_calls");
        response.Usage!.TotalTokens.Should().Be(4);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ChunkWithoutCandidates_IsIgnored()
    {
        var sse =
            "data: {\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hi\"}]},\"finishReason\":\"STOP\"}]}\n\n";
        var handler = new CapturingHandler(_ => ChatHttpResponses.Sse(sse));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        var response = await client.StreamCompletionAsync(UserRequest("gemini-2.5-flash"), _ => { });

        response.FirstChoice!.Message.GetText().Should().Be("hi");
        response.Usage!.TotalTokens.Should().Be(2);
    }

    [TestMethod]
    public async Task GetCompletionAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(_ => ChatHttpResponses.Json("denied", HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler);
        var client = Client(handler, httpClient);

        Func<Task> act = () => client.GetCompletionAsync(UserRequest("gemini-2.5-flash"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*failed (403)*");
    }
}
