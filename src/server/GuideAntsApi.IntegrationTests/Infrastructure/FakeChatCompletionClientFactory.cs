using System.Text.Json;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

/// <summary>
/// Configurable fake chat client used by integration tests. Default behavior is unchanged
/// (single-chunk stream returning "Test assistant response.").
/// </summary>
internal enum FakeChatScenario
{
    Default,
    SlowCancellableStream,
    NonCooperativeStream,
    ToolCallsCancelBeforeExecution,
    ToolCallThenReply,
    ThinkingStream,
    LongThinkingStream,
    RepeatedToolCalls,
    PartialTimeoutStream,
    NestedAgentBlocking,
}

internal sealed class FakeChatCompletionBehavior
{
    public static FakeChatCompletionBehavior Instance { get; } = new();

    public FakeChatScenario Scenario { get; set; } = FakeChatScenario.Default;
    public int ChunkDelayMs { get; set; } = 80;
    public string SlowStreamText { get; set; } = "Partial assistant response before cancel.";
    public string FinalAssistantText { get; set; } = "Test assistant response.";
    public string ThinkingText { get; set; } = "integration reasoning step";
    public string ToolCallId { get; set; } = "call_integration_test";
    public string ToolFunctionName { get; set; } = "IntegrationTestTool";
    public string ToolArgumentsJson { get; set; } = """{"query":"integration"}""";
    public TaskCompletionSource<bool> NestedCompletionStarted { get; private set; } = CreateSignal();
    public TaskCompletionSource<bool> NestedCompletionCancelled { get; private set; } = CreateSignal();
    public TaskCompletionSource<bool> NonCooperativeStreamStarted { get; private set; } = CreateSignal();
    public TaskCompletionSource<bool> NonCooperativeStreamRelease { get; private set; } = CreateSignal();
    public TaskCompletionSource<bool> SlowStreamFirstChunkEmitted { get; private set; } = CreateSignal();
    public IReadOnlyList<ChatMessage>? LastRequestMessages { get; private set; }

    /// <summary>
    /// Invoked synchronously immediately before a tool_calls response is returned.
    /// Tests can cancel the stream token here to exercise incomplete-tool-call pruning.
    /// </summary>
    public Action? OnToolCallsReturning { get; set; }

    private int _callIndex;
    private int _toolChoiceNoneRequestCount;

    public int NextCallIndex() => Interlocked.Increment(ref _callIndex);

    public int ToolChoiceNoneRequestCount => Volatile.Read(ref _toolChoiceNoneRequestCount);

    public int RepeatedToolCallCount => Volatile.Read(ref _callIndex);

    public void RecordToolChoiceRequest(string? toolChoice)
    {
        if (string.Equals(toolChoice, "none", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _toolChoiceNoneRequestCount);
        }
    }

    public void Reset()
    {
        Scenario = FakeChatScenario.Default;
        ChunkDelayMs = 80;
        SlowStreamText = "Partial assistant response before cancel.";
        FinalAssistantText = "Test assistant response.";
        ThinkingText = "integration reasoning step";
        ToolCallId = "call_integration_test";
        ToolFunctionName = "IntegrationTestTool";
        ToolArgumentsJson = """{"query":"integration"}""";
        NestedCompletionStarted = CreateSignal();
        NestedCompletionCancelled = CreateSignal();
        NonCooperativeStreamStarted = CreateSignal();
        NonCooperativeStreamRelease = CreateSignal();
        SlowStreamFirstChunkEmitted = CreateSignal();
        OnToolCallsReturning = null;
        LastRequestMessages = null;
        Interlocked.Exchange(ref _callIndex, 0);
        Interlocked.Exchange(ref _toolChoiceNoneRequestCount, 0);
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SignalNestedCompletionStarted() => NestedCompletionStarted.TrySetResult(true);

    public void CaptureRequest(ChatCompletionRequest request) =>
        LastRequestMessages = request.Messages.ToList();
}

internal sealed class FakeChatCompletionClientFactory : IChatCompletionClientFactory
{
    public string? DefaultDeploymentId => "test-deployment";

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        return new FakeChatCompletionClient(FakeChatCompletionBehavior.Instance);
    }
}

internal sealed class FakeChatCompletionClient : IChatCompletionClient
{
    private readonly FakeChatCompletionBehavior _behavior;

    public bool SupportsToolChoiceNone => true;

    public FakeChatCompletionClient(FakeChatCompletionBehavior behavior)
    {
        _behavior = behavior;
    }

    public Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        _behavior.CaptureRequest(request);
        if (_behavior.Scenario == FakeChatScenario.NestedAgentBlocking)
        {
            return WaitForNestedCompletionAsync(cancellationToken);
        }

        return Task.FromResult(CreateDefaultResponse());
    }

    public Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        _behavior.CaptureRequest(request);
        return _behavior.Scenario switch
        {
            FakeChatScenario.SlowCancellableStream => SlowCancellableStreamAsync(onChunk, cancellationToken),
            FakeChatScenario.NonCooperativeStream => NonCooperativeStreamAsync(onChunk),
            FakeChatScenario.ToolCallsCancelBeforeExecution => ToolCallsCancelBeforeExecutionAsync(onChunk),
            FakeChatScenario.ToolCallThenReply => ToolCallThenReplyAsync(onChunk, cancellationToken),
            FakeChatScenario.ThinkingStream => ThinkingStreamAsync(onChunk, cancellationToken),
            FakeChatScenario.LongThinkingStream => LongThinkingStreamAsync(onChunk, cancellationToken),
            FakeChatScenario.RepeatedToolCalls => RepeatedToolCallsAsync(request, onChunk),
            FakeChatScenario.PartialTimeoutStream => PartialTimeoutStreamAsync(onChunk, cancellationToken),
            FakeChatScenario.NestedAgentBlocking => NestedAgentBlockingStreamAsync(onChunk),
            _ => DefaultStreamAsync(onChunk)
        };
    }

    private Task<ChatCompletionResponse> NestedAgentBlockingStreamAsync(
        Action<ChatCompletionChunk> onChunk)
    {
        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, "Reading the web page."), null)
        ]));
        return Task.FromResult(CreateToolCallsResponse());
    }

    private async Task<ChatCompletionResponse> WaitForNestedCompletionAsync(
        CancellationToken cancellationToken)
    {
        _behavior.SignalNestedCompletionStarted();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _behavior.NestedCompletionCancelled.TrySetResult(true);
            throw;
        }

        return CreateDefaultResponse();
    }

    private Task<ChatCompletionResponse> DefaultStreamAsync(Action<ChatCompletionChunk> onChunk)
    {
        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, _behavior.FinalAssistantText), null)
        ]));

        return Task.FromResult(CreateDefaultResponse());
    }

    private async Task<ChatCompletionResponse> SlowCancellableStreamAsync(
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken)
    {
        foreach (var word in _behavior.SlowStreamText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, word + " "), null)
            ]));
            _behavior.SlowStreamFirstChunkEmitted.TrySetResult(true);
            await Task.Delay(_behavior.ChunkDelayMs, cancellationToken);
        }

        return CreateResponse(_behavior.SlowStreamText, finishReason: "stop");
    }

    private async Task<ChatCompletionResponse> NonCooperativeStreamAsync(
        Action<ChatCompletionChunk> onChunk)
    {
        _behavior.NonCooperativeStreamStarted.TrySetResult(true);
        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, "Provider ignored cancellation."), null)
        ]));

        // Deliberately ignore the worker token. The engine must let the cancellation request win
        // when this provider eventually returns a normal response.
        await _behavior.NonCooperativeStreamRelease.Task;
        return CreateResponse("Provider completed after cancellation.", finishReason: "stop");
    }

    private Task<ChatCompletionResponse> ToolCallsCancelBeforeExecutionAsync(Action<ChatCompletionChunk> onChunk)
    {
        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, "Calling tools."), null)
        ]));

        _behavior.OnToolCallsReturning?.Invoke();
        return Task.FromResult(CreateToolCallsResponse());
    }

    private async Task<ChatCompletionResponse> ToolCallThenReplyAsync(
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken)
    {
        var callIndex = _behavior.NextCallIndex();
        if (callIndex == 1)
        {
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, "Invoking tool."), null)
            ]));
            return CreateToolCallsResponse(callIndex);
        }

        foreach (var word in _behavior.FinalAssistantText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, word + " "), null)
            ]));
            await Task.Delay(10, cancellationToken);
        }

        return CreateResponse(_behavior.FinalAssistantText, finishReason: "stop");
    }

    private Task<ChatCompletionResponse> RepeatedToolCallsAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk)
    {
        _behavior.RecordToolChoiceRequest(request.ToolChoice);
        if (string.Equals(request.ToolChoice, "none", StringComparison.Ordinal))
        {
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, _behavior.FinalAssistantText), null)
            ]));
            return Task.FromResult(CreateResponse(_behavior.FinalAssistantText, finishReason: "stop"));
        }

        var callIndex = _behavior.NextCallIndex();
        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, $"Tool round {callIndex}."), null)
        ]));
        return Task.FromResult(CreateToolCallsResponse(callIndex));
    }

    private async Task<ChatCompletionResponse> ThinkingStreamAsync(
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken)
    {
        foreach (var word in _behavior.ThinkingText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, word + " "), "thinking")
            ]));
            await Task.Delay(10, cancellationToken);
        }

        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, _behavior.FinalAssistantText), null)
        ]));

        var thinkingBlocks = new List<ChatThinkingBlock>
        {
            ChatThinkingBlock.ForThinking(_behavior.ThinkingText, "integration-sig")
        };

        var message = new ChatMessage(
            ChatRole.Assistant,
            [new ChatContent(_behavior.FinalAssistantText)],
            toolCalls: null,
            thinkingBlocks: thinkingBlocks);

        return new ChatCompletionResponse(
            [new ChatChoice(message, "stop")],
            CreateUsage());
    }

    private async Task<ChatCompletionResponse> LongThinkingStreamAsync(
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken)
    {
        foreach (var word in _behavior.ThinkingText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, word + " "), "thinking")
            ]));
            await Task.Delay(_behavior.ChunkDelayMs, cancellationToken);
        }

        onChunk(new ChatCompletionChunk(
        [
            new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, _behavior.FinalAssistantText), null)
        ]));

        return CreateResponse(_behavior.FinalAssistantText, finishReason: "stop");
    }

    private async Task<ChatCompletionResponse> PartialTimeoutStreamAsync(
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken)
    {
        foreach (var word in _behavior.SlowStreamText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, word + " "), null)
            ]));
            await Task.Delay(10, cancellationToken);
        }

        var partialResponse = CreateResponse(_behavior.SlowStreamText, finishReason: "stop");
        throw new LlamaInferenceTimeoutException(
            "test-deployment",
            30,
            partialResponse);
    }

    private ChatCompletionResponse CreateDefaultResponse() =>
        CreateResponse(_behavior.FinalAssistantText, finishReason: "stop");

    private ChatCompletionResponse CreateResponse(string content, string finishReason)
    {
        return new ChatCompletionResponse(
            [new ChatChoice(new ChatMessage(ChatRole.Assistant, content), finishReason)],
            CreateUsage());
    }

    private ChatCompletionResponse CreateToolCallsResponse(int callIndex = 1)
    {
        using var argumentsDocument = JsonDocument.Parse(_behavior.ToolArgumentsJson);
        var toolCallId = _behavior.Scenario == FakeChatScenario.RepeatedToolCalls
            ? $"{_behavior.ToolCallId}_{callIndex}"
            : _behavior.ToolCallId;

        var toolCall = new ChatToolCall
        {
            Id = toolCallId,
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = _behavior.ToolFunctionName,
                Arguments = argumentsDocument.RootElement.Clone()
            }
        };

        var message = new ChatMessage(
            ChatRole.Assistant,
            [new ChatContent("Calling tools.")],
            toolCalls: [toolCall]);

        return new ChatCompletionResponse(
            [new ChatChoice(message, "tool_calls")],
            CreateUsage());
    }

    private static ChatCompletionUsage CreateUsage() =>
        new()
        {
            PromptTokens = 1,
            CompletionTokens = 1,
            TotalTokens = 2
        };
}
