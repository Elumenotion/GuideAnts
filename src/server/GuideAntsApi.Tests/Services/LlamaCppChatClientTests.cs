using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class LlamaCppChatClientTests
{
    [TestMethod]
    public async Task GetCompletionAsync_MapsToolCallsThinkingAndUsage()
    {
        const string responseJson = """
            {
              "id": "chatcmpl-1",
              "choices": [
                {
                  "finish_reason": "tool_calls",
                  "message": {
                    "role": "assistant",
                    "content": "Done",
                    "reasoning_content": "Hidden reasoning",
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "compute",
                          "arguments": "{\"x\":1}"
                        }
                      }
                    ]
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 5,
                "total_tokens": 15
              }
            }
            """;

        var handler = new StaticResponseHandler(_ => JsonResponse(responseJson));
        var client = CreateClient(handler);
        var request = BuildRequest();

        var response = await client.GetCompletionAsync(request);

        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.GetText().Should().Be("Done");
        response.FirstChoice.Message.ThinkingBlocks.Should().NotBeNull();
        response.FirstChoice.Message.ThinkingBlocks!.Should().HaveCount(1);
        response.FirstChoice.Message.ThinkingBlocks[0].Thinking.Should().Be("Hidden reasoning");
        response.FirstChoice.Message.ToolCalls.Should().NotBeNull();
        response.FirstChoice.Message.ToolCalls!.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls[0].Id.Should().Be("call_1");
        response.FirstChoice.Message.ToolCalls[0].Function.Name.Should().Be("compute");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("x").GetInt32().Should().Be(1);
        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(10);
        response.Usage.CompletionTokens.Should().Be(5);
        response.Usage.TotalTokens.Should().Be(15);
    }

    [TestMethod]
    public async Task GetCompletionAsync_SendsDisableThinkingPayload_WhenReasoningIsNone()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b",
            reasoningEffort: "None");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;

        root.GetProperty("reasoning_format").GetString().Should().Be("none");
        root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCompletionAsync_AppliesEnabledThinkingActions_WhenReasoningIsEnabled()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b",
            reasoningEffort: "enabled");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;

        root.TryGetProperty("reasoning_format", out _).Should().BeFalse();
        root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_UsesDefaultChoice_WhenReasoningIsNotSpecified()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;

        root.TryGetProperty("reasoning_format", out _).Should().BeFalse();
        root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_UsesGemmaDisablePolicy_WhenReasoningIsNone()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler, profileData: CreateGemma4ProfileData());
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "gemma-4",
            reasoningEffort: "none");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;

        root.GetProperty("reasoning_format").GetString().Should().Be("none");
        root.TryGetProperty("chat_template_kwargs", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCompletionAsync_UsesDeploymentIdOverRequestModel()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler, deploymentId: "Qwen3.5-27B-Q6_K");
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("Qwen3.5-27B-Q6_K");
    }

    [TestMethod]
    public async Task GetCompletionAsync_MapsImageAttachments_AsOpenAiImageUrlContentBlocks()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(
                    ChatRole.User,
                    new List<ChatContent>
                    {
                        new("Describe this image."),
                        new(new ChatImageUrl("data:image/png;base64,AAAA"))
                    })
            ],
            model: "qwen3.5-27b");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var firstMessage = doc.RootElement.GetProperty("messages")[0];
        var content = firstMessage.GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.Array);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("Describe this image.");
        content[1].GetProperty("type").GetString().Should().Be("image_url");
        content[1].GetProperty("image_url").GetProperty("url").GetString().Should().Be("data:image/png;base64,AAAA");
    }

    [TestMethod]
    public async Task GetCompletionAsync_StripsLeadingEmptyThinkBlocks_Only()
    {
        const string responseJson = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": {
                    "role": "assistant",
                    "content": " \n<think>   </think>\nAnswer"
                  }
                }
              ]
            }
            """;

        var handler = new StaticResponseHandler(_ => JsonResponse(responseJson));
        var client = CreateClient(handler);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b"));

        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.Message.GetText().Should().Be("Answer");
    }

    [TestMethod]
    public async Task GetCompletionAsync_DoesNotStripNonEmptyThinkBlocks()
    {
        const string responseJson = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": {
                    "role": "assistant",
                    "content": "<think>internal reasoning</think> After"
                  }
                }
              ]
            }
            """;

        var handler = new StaticResponseHandler(_ => JsonResponse(responseJson));
        var client = CreateClient(handler);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b"));

        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.Message.GetText().Should().Be("<think>internal reasoning</think> After");
    }

    [TestMethod]
    public async Task GetCompletionAsync_PreservesToolCalls_WhenLeadingContentIsOnlyEmptyThinkBlock()
    {
        const string responseJson = """
            {
              "choices": [
                {
                  "finish_reason": "tool_calls",
                  "message": {
                    "role": "assistant",
                    "content": "<think>   </think>",
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "compute",
                          "arguments": "{\"x\":1}"
                        }
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var handler = new StaticResponseHandler(_ => JsonResponse(responseJson));
        var client = CreateClient(handler);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "qwen3.5-27b"));

        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.GetText().Should().BeEmpty();
        response.FirstChoice.Message.ToolCalls.Should().NotBeNull();
        response.FirstChoice.Message.ToolCalls!.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls[0].Function.Name.Should().Be("compute");
    }

    [TestMethod]
    public async Task GetCompletionAsync_StripsConfiguredAdditionalPatterns_OnlyWhenLeadingAndEmpty()
    {
        const string responseJson = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": {
                    "role": "assistant",
                    "content": "<scratch>   </scratch>B"
                  }
                }
              ]
            }
            """;

        var handler = new StaticResponseHandler(_ => JsonResponse(responseJson));
        var config = new LlamaCppConfig
        {
            BaseUrl = "http://localhost:8000",
            ApiKey = "test-key",
            TimeoutSeconds = 300,
            OutputStripPatterns = [@"<scratch>[\s\S]*?</scratch>"]
        };
        var client = CreateClient(handler, profileData: CreateGemma4ProfileData(), config: config);

        var response = await client.GetCompletionAsync(new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            model: "gemma-4"));

        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.Message.GetText().Should().Be("B");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_AggregatesAssistantThinkingAndToolCalls()
    {
        const string sse = """
            data: {"choices":[{"delta":{"reasoning_content":"step-1 "}}]}

            data: {"choices":[{"delta":{"content":"Hello "}}]}

            data: {"choices":[{"delta":{"content":"world","tool_calls":[{"index":0,"id":"call_abc","function":{"name":"math","arguments":"{\"n\":"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"42}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

            data: [DONE]

            """;

        var handler = new StaticResponseHandler(_ => SseResponse(sse));
        var client = CreateClient(handler);
        var request = BuildRequest();
        var chunks = new List<ChatCompletionChunk>();

        var response = await client.StreamCompletionAsync(request, chunk => chunks.Add(chunk));

        chunks.Should().NotBeEmpty();
        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.GetText().Should().Be("Hello world");
        response.FirstChoice.Message.ThinkingBlocks.Should().NotBeNull();
        response.FirstChoice.Message.ThinkingBlocks![0].Thinking.Should().Be("step-1 ");
        response.FirstChoice.Message.ToolCalls.Should().NotBeNull();
        response.FirstChoice.Message.ToolCalls!.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls[0].Id.Should().Be("call_abc");
        response.FirstChoice.Message.ToolCalls[0].Function.Name.Should().Be("math");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("n").GetInt32().Should().Be(42);
        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(2);
        response.Usage.CompletionTokens.Should().Be(3);
        response.Usage.TotalTokens.Should().Be(5);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_StripsLeadingEmptyThinkBlocks_OnTheFly()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":" \n<th"}}]}

            data: {"choices":[{"delta":{"content":"ink>  "}}]}

            data: {"choices":[{"delta":{"content":"</think>"}}]}

            data: {"choices":[{"delta":{"content":"After"}}]}

            data: {"choices":[{"finish_reason":"stop","delta":{}}]}

            data: [DONE]

            """;

        var handler = new StaticResponseHandler(_ => SseResponse(sse));
        var client = CreateClient(handler);
        var streamedText = new StringBuilder();

        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "test")], model: "qwen3.5-27b"),
            chunk =>
            {
                var delta = chunk.FirstChoice?.Delta?.Content;
                if (!string.IsNullOrEmpty(delta))
                {
                    delta.Should().NotContain("<think>");
                    delta.Should().NotContain("</think>");
                    streamedText.Append(delta);
                }
            });

        streamedText.ToString().Should().Be("After");
        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.Message.GetText().Should().Be("After");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_PreservesToolCalls_WhenStreamingOnlyLeadingEmptyThinkBlock()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"<think>   "}}]}

            data: {"choices":[{"delta":{"content":"</think>","tool_calls":[{"index":0,"id":"call_abc","function":{"name":"math","arguments":"{\"n\":"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"42}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var handler = new StaticResponseHandler(_ => SseResponse(sse));
        var client = CreateClient(handler);
        var streamedText = new StringBuilder();

        var response = await client.StreamCompletionAsync(
            new ChatCompletionRequest(messages: [new ChatMessage(ChatRole.User, "test")], model: "qwen3.5-27b"),
            chunk =>
            {
                var delta = chunk.FirstChoice?.Delta?.Content;
                if (!string.IsNullOrEmpty(delta))
                {
                    streamedText.Append(delta);
                }
            });

        streamedText.ToString().Should().BeEmpty();
        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.FinishReason.Should().Be("tool_calls");
        response.FirstChoice.Message.GetText().Should().BeEmpty();
        response.FirstChoice.Message.ToolCalls.Should().NotBeNull();
        response.FirstChoice.Message.ToolCalls!.Should().HaveCount(1);
        response.FirstChoice.Message.ToolCalls[0].Id.Should().Be("call_abc");
        response.FirstChoice.Message.ToolCalls[0].Function.Name.Should().Be("math");
        response.FirstChoice.Message.ToolCalls[0].Function.Arguments.GetProperty("n").GetInt32().Should().Be(42);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_PreservesWhitespaceOnlyDeltas()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Line 1"}}]}

            data: {"choices":[{"delta":{"content":"\n"}}]}

            data: {"choices":[{"delta":{"content":"Line 2"}}]}

            data: {"choices":[{"finish_reason":"stop","delta":{}}]}

            data: [DONE]

            """;

        var handler = new StaticResponseHandler(_ => SseResponse(sse));
        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "test")],
            model: "qwen3.5-27b");

        var response = await client.StreamCompletionAsync(request, _ => { });

        response.FirstChoice.Should().NotBeNull();
        response.FirstChoice!.Message.GetText().Should().Be("Line 1\nLine 2");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_SendsStreamOptionsIncludeUsage()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return SseResponse("""
                data: {"choices":[{"finish_reason":"stop","delta":{}}]}

                data: [DONE]
                """);
        });

        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "test")],
            model: "qwen3.5-27b");

        await client.StreamCompletionAsync(request, _ => { });

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        root.GetProperty("stream").GetBoolean().Should().BeTrue();
        root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_SendsParallelToolCalls_WhenEnabledForModel()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler, parallelToolCalls: true);
        var request = BuildRequest();

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        root.GetProperty("parallel_tool_calls").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCompletionAsync_NormalizesSystemAndDeveloperMessages_ToSingleLeadingSystemMessage()
    {
        string? capturedBody = null;
        var handler = new StaticResponseHandler(async httpRequest =>
        {
            capturedBody = await httpRequest.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        var client = CreateClient(handler);
        var request = new ChatCompletionRequest(
            messages:
            [
                new ChatMessage(ChatRole.User, "hello"),
                new ChatMessage(ChatRole.Developer, "dev rules"),
                new ChatMessage(ChatRole.System, "system rules"),
                new ChatMessage(ChatRole.User, "next")
            ],
            model: "qwen3.5-27b");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var messages = doc.RootElement.GetProperty("messages");
        messages[0].GetProperty("role").GetString().Should().Be("system");
        var firstContent = messages[0].GetProperty("content").GetString();
        firstContent.Should().NotBeNull();
        firstContent!.Should().Contain("dev rules");
        firstContent.Should().Contain("system rules");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ClassifiesCudaOomResponseBody_AsOutOfMemoryCrash()
    {
        const string cudaOomBody =
            "{\"error\":{\"message\":\"CUDA error: out of memory\",\"type\":\"invalid_request_error\"}}";
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(cudaOomBody, Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.OutOfMemory);
        ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ex.Which.Message.Should().Contain("out of GPU memory");
        // Upstream detail is the clipped error.message, not the 1MB diagnostic dump.
        ex.Which.UpstreamDetail.Should().Be("CUDA error: out of memory");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_ClassifiesGenericInternalError_AsCrash()
    {
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal server error", Encoding.UTF8, "text/plain")
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.Crashed);
        ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [TestMethod]
    public async Task StreamCompletionAsync_LeavesClientErrors_AsHttpRequestException()
    {
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad model id", Encoding.UTF8, "text/plain")
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        // 4xx is a caller-side bug, not a runtime state signal: neither the crash modal nor the
        // load dialog must fire. Body lacks any of the NotReady markers so the default path wins.
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [TestMethod]
    public async Task StreamCompletionAsync_Classifies400NoModelLoaded_AsNotReady()
    {
        // Real-world body llama-server returns from /v1/chat/completions when no model is loaded.
        const string noModelBody =
            "{\"error\":{\"code\":400,\"message\":\"the server does not have a model loaded\",\"type\":\"invalid_request_error\"}}";
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(noModelBody, Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.NotReady);
        ex.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.Which.UpstreamDetail.Should().Be("the server does not have a model loaded");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_Classifies400ModelIsNotLoaded_AsNotReady()
    {
        // Some llama-server builds emit this shorter variant.
        const string noModelBody =
            "{\"message\":\"model is not loaded\"}";
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(noModelBody, Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.NotReady);
        ex.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.Which.UpstreamDetail.Should().Be("model is not loaded");
    }

    [TestMethod]
    public async Task StreamCompletionAsync_DoesNotClassifyOther400s_AsNotReady()
    {
        // A caller bug (e.g. malformed tool schema) must stay as HttpRequestException so it is
        // routed to the generic error surface rather than the load dialog.
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"invalid tool schema\",\"type\":\"invalid_request_error\"}}",
                Encoding.UTF8,
                "application/json")
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [TestMethod]
    public async Task StreamCompletionAsync_MapsMidStreamSocketDrop_ToCrashedException()
    {
        var handler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingStream()) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } }
        });
        var client = CreateClient(handler);

        var act = async () => await client.StreamCompletionAsync(BuildRequest(), _ => { });

        var ex = await act.Should().ThrowAsync<LlamaRuntimeCrashedException>();
        ex.Which.Reason.Should().Be(LlamaRuntimeCrashReason.Crashed);
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset by peer");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new IOException("connection reset by peer");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static LlamaCppChatClient CreateClient(
        HttpMessageHandler handler,
        string deploymentId = "qwen3.5-27b",
        LlamaCppRuntimeProfileData? profileData = null,
        bool parallelToolCalls = false,
        LlamaCppConfig? config = null)
    {
        var httpClient = new HttpClient(handler);
        config ??= new LlamaCppConfig
        {
            BaseUrl = "http://localhost:8000",
            ApiKey = "test-key",
            TimeoutSeconds = 300
        };

        profileData ??= CreateQwen35ProfileData();

        return new LlamaCppChatClient(httpClient, config, deploymentId, profileData, parallelToolCalls);
    }

    private static LlamaCppRuntimeProfileData CreateQwen35ProfileData()
    {
        return new LlamaCppRuntimeProfileData(
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
                }));
    }

    private static LlamaCppRuntimeProfileData CreateGemma4ProfileData()
    {
        return new LlamaCppRuntimeProfileData(
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
                }));
    }

    private static ChatCompletionRequest BuildRequest()
    {
        var paramsSchema = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"number"}}}""");
        return new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(new ChatFunctionDefinition("compute", "compute value", paramsSchema))],
            model: "qwen3.5-27b");
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage SseResponse(string payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this(request => Task.FromResult(responseFactory(request)))
        {
        }

        public StaticResponseHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _responseFactory(request);
        }
    }
}
