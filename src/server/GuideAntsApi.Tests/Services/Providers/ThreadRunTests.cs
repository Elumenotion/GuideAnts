using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using AntRunner.ToolCalling;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Deterministic, network-free coverage of <see cref="ThreadRun"/> helpers.
/// The execution engine itself depends on assistant storage / live providers, so these
/// tests target the pure static helpers (reachable via reflection) and the public
/// request-builder cache surface.
/// </summary>
[TestClass]
public sealed class ThreadRunTests
{
    private static readonly Type ThreadRunType = typeof(ThreadRun);

    private static T Invoke<T>(string name, params object?[] args)
    {
        var method = ThreadRunType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ThreadRun), name);
        return (T)method.Invoke(null, args)!;
    }

    [TestMethod]
    public void NormalizeAssistantText_ReplacesEmDashWithEnDash()
    {
        Invoke<string>("NormalizeAssistantText", "before\u2014after").Should().Be("before\u2013after");
    }

    [TestMethod]
    public void NormalizeAssistantText_NullOrEmpty_ReturnsEmpty()
    {
        Invoke<string>("NormalizeAssistantText", new object?[] { null }).Should().Be(string.Empty);
        Invoke<string>("NormalizeAssistantText", string.Empty).Should().Be(string.Empty);
    }

    [TestMethod]
    public void NormalizeAssistantText_LeavesPlainTextUnchanged()
    {
        Invoke<string>("NormalizeAssistantText", "no dashes here").Should().Be("no dashes here");
    }

    [TestMethod]
    public void IsFatalChatRunException_DoesNotTerminateConversationForLlamaInferenceTimeout()
    {
        var timeout = new LlamaInferenceTimeoutException("qwen3.5-27b", 600);
        var nestedFailure = new ChatConversationException(timeout, chatRunOutput: null);
        var toolInvocationFailure = new TargetInvocationException(nestedFailure);

        Invoke<bool>("IsFatalChatRunException", toolInvocationFailure).Should().BeFalse();
    }

    [TestMethod]
    public void IsFatalChatRunException_DoesNotTerminateConversationForOrdinaryToolFailure()
    {
        var toolFailure = new ChatConversationException(
            new InvalidOperationException("tool failed"),
            chatRunOutput: null);

        Invoke<bool>("IsFatalChatRunException", toolFailure).Should().BeFalse();
    }

    [TestMethod]
    public void FormatFileChangesConsole_RendersNewAndModifiedSections()
    {
        var result = Invoke<string>(
            "FormatFileChangesConsole",
            new List<string> { "a.txt", "b.txt" },
            new List<string> { "c.txt" });

        result.Should().StartWith("```console");
        result.Should().Contain("# New Files");
        result.Should().Contain("a.txt");
        result.Should().Contain("b.txt");
        result.Should().Contain("# Modified Files");
        result.Should().Contain("c.txt");
        result.Should().EndWith("```");
    }

    [TestMethod]
    public void FormatFileChangesConsole_OmitsEmptySections()
    {
        var result = Invoke<string>(
            "FormatFileChangesConsole",
            new List<string>(),
            new List<string> { "only-modified.txt" });

        result.Should().NotContain("# New Files");
        result.Should().Contain("# Modified Files");
        result.Should().Contain("only-modified.txt");
    }

    [TestMethod]
    public void BuildEvaluatorDialog_ExcludesEmptyTerminalAssistantMessage()
    {
        var output = new ChatRunOutput
        {
            LastMessage = string.Empty,
            ConversationMessages =
            [
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.User,
                    Message = "create the podcast"
                },
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.Assistant,
                    Message = string.Empty
                },
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.Tool,
                    Message = "podcast created"
                },
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.Assistant,
                    Message = string.Empty
                }
            ]
        };

        var result = Invoke<string>("BuildEvaluatorDialog", output);

        result.Should().Contain("User: create the podcast");
        result.Should().Contain("Tool: podcast created");
        result.Should().EndWith("Tool: podcast created\n");
    }

    [TestMethod]
    public void BuildEvaluatorDialog_RemovesOnlyTerminalResponse_WhenTextAppearsEarlier()
    {
        var output = new ChatRunOutput
        {
            LastMessage = "same text",
            ConversationMessages =
            [
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.User,
                    Message = "same text"
                },
                new ThreadConversationMessage
                {
                    MessageType = ThreadConversationMessageType.Assistant,
                    Message = "same text"
                }
            ]
        };

        var result = Invoke<string>("BuildEvaluatorDialog", output);

        result.Should().Contain("User: same text");
        result.Should().NotContain("Assistant: same text");
    }

    [TestMethod]
    public void ApplyAccumulatedFileChanges_PopulatesPartialResult()
    {
        var output = new ChatRunOutput();

        Invoke<object>(
            "ApplyAccumulatedFileChanges",
            output,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "podcast.wav" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notes.md" });

        output.NewFiles.Should().Equal("podcast.wav");
        output.ModifiedFiles.Should().Equal("notes.md");
    }

    [TestMethod]
    public void BuildSamplingParameters_KeepsNumericValuesOnly()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["temperature"] = JsonSerializer.SerializeToElement(0.7),
            ["top_p"] = JsonSerializer.SerializeToElement(0.9),
            ["reasoning_effort"] = JsonSerializer.SerializeToElement("high"),
            ["enabled"] = JsonSerializer.SerializeToElement(true)
        };

        var result = Invoke<IReadOnlyDictionary<string, double>?>("BuildSamplingParameters", input);

        result.Should().NotBeNull();
        result!.Should().ContainKey("temperature").WhoseValue.Should().Be(0.7);
        result.Should().ContainKey("top_p").WhoseValue.Should().Be(0.9);
        result.Should().NotContainKey("reasoning_effort");
        result.Should().NotContainKey("enabled");
    }

    [TestMethod]
    public void BuildSamplingParameters_ReturnsNull_WhenNoNumericValues()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["reasoning_effort"] = JsonSerializer.SerializeToElement("high")
        };

        var result = Invoke<IReadOnlyDictionary<string, double>?>("BuildSamplingParameters", input);

        result.Should().BeNull();
    }

    [TestMethod]
    public void TryGetStringParameter_ReturnsTrimmedString()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["reasoning_effort"] = JsonSerializer.SerializeToElement("  high  ")
        };

        Invoke<string?>("TryGetStringParameter", input, "reasoning_effort").Should().Be("high");
    }

    [TestMethod]
    public void TryGetStringParameter_ReturnsNull_ForMissingWhitespaceOrNonString()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["blank"] = JsonSerializer.SerializeToElement("   "),
            ["num"] = JsonSerializer.SerializeToElement(5)
        };

        Invoke<string?>("TryGetStringParameter", input, "missing").Should().BeNull();
        Invoke<string?>("TryGetStringParameter", input, "blank").Should().BeNull();
        Invoke<string?>("TryGetStringParameter", input, "num").Should().BeNull();
    }

    [TestMethod]
    public void GetStreamRetryDelay_ClampsToConfiguredDelays()
    {
        Invoke<TimeSpan>("GetStreamRetryDelay", 0).Should().Be(TimeSpan.FromSeconds(5));
        Invoke<TimeSpan>("GetStreamRetryDelay", 1).Should().Be(TimeSpan.FromSeconds(5));
        Invoke<TimeSpan>("GetStreamRetryDelay", 2).Should().Be(TimeSpan.FromSeconds(10));
        Invoke<TimeSpan>("GetStreamRetryDelay", 5).Should().Be(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public void IsTransientStreamFailure_TrueForRetryableConditions()
    {
        var ct = CancellationToken.None;
        Invoke<bool>("IsTransientStreamFailure", new HttpRequestException("boom"), ct).Should().BeTrue();
        Invoke<bool>("IsTransientStreamFailure",
            new HttpRequestException("500", null, System.Net.HttpStatusCode.InternalServerError), ct).Should().BeTrue();
        Invoke<bool>("IsTransientStreamFailure",
            new HttpRequestException("429", null, System.Net.HttpStatusCode.TooManyRequests), ct).Should().BeTrue();
        Invoke<bool>("IsTransientStreamFailure",
            new HttpRequestException("408", null, System.Net.HttpStatusCode.RequestTimeout), ct).Should().BeTrue();
        Invoke<bool>("IsTransientStreamFailure", new IOException("reset"), ct).Should().BeTrue();
        Invoke<bool>("IsTransientStreamFailure",
            new System.Net.Sockets.SocketException(), ct).Should().BeTrue();
        Invoke<bool>("IsTransientStreamFailure",
            new InvalidOperationException("outer", new IOException("inner")), ct).Should().BeTrue();
    }

    [TestMethod]
    public void IsTransientStreamFailure_FalseForNonRetryableConditions()
    {
        var ct = CancellationToken.None;
        Invoke<bool>("IsTransientStreamFailure",
            new HttpRequestException("400", null, System.Net.HttpStatusCode.BadRequest), ct).Should().BeFalse();
        Invoke<bool>("IsTransientStreamFailure", new OperationCanceledException(), ct).Should().BeFalse();
        Invoke<bool>("IsTransientStreamFailure", new InvalidOperationException("plain"), ct).Should().BeFalse();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Invoke<bool>("IsTransientStreamFailure", new HttpRequestException("boom"), cts.Token).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseAbsoluteUri_ParsesAbsoluteAndSchemelessHosts()
    {
        var absoluteArgs = new object?[] { "https://api.example.com/path", null };
        Invoke<bool>("TryParseAbsoluteUri", absoluteArgs).Should().BeTrue();
        ((Uri)absoluteArgs[1]!).Host.Should().Be("api.example.com");

        var schemelessArgs = new object?[] { "api.example.com", null };
        Invoke<bool>("TryParseAbsoluteUri", schemelessArgs).Should().BeTrue();
        ((Uri)schemelessArgs[1]!).Host.Should().Be("api.example.com");
    }

    [TestMethod]
    public void GetAuthorityCandidates_YieldsAuthorityThenHost()
    {
        var result = Invoke<IEnumerable<string>>("GetAuthorityCandidates", "https://api.example.com:8443/v1").ToList();

        result.Should().Contain("api.example.com:8443");
        result.Should().Contain("api.example.com");
    }

    [TestMethod]
    public void GetAuthorityCandidates_EmptyForBlankInput()
    {
        Invoke<IEnumerable<string>>("GetAuthorityCandidates", "").Should().BeEmpty();
    }

    [TestMethod]
    public void ExtractSandboxInitFilename_ExtractsHostAndPath()
    {
        Invoke<string>("ExtractSandboxInitFilename", "sandbox://init.py").Should().Be("init.py");
        Invoke<string>("ExtractSandboxInitFilename", "sandbox://folder/start.py").Should().Be("start.py");
    }

    [TestMethod]
    public void BuildOutboundChatRequestLogPayload_IncludesMessagesToolsAndSampling()
    {
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(ChatRole.User, "visibility-probe-xyz")],
            tools:
            [
                new ChatToolDefinition(
                    new ChatFunctionDefinition(
                        "lookup",
                        "Lookup something",
                        JsonNode.Parse("""{"type":"object"}""")))
            ],
            model: "test-model",
            reasoningEffort: "medium",
            samplingParameters: new Dictionary<string, double> { ["temperature"] = 0.2 },
            toolChoice: "none");

        var payload = Invoke<string>("BuildOutboundChatRequestLogPayload", request);

        payload.Should().Contain("visibility-probe-xyz");
        payload.Should().Contain("test-model");
        payload.Should().Contain("lookup");
        payload.Should().Contain("sampling_parameters");
        payload.Should().Contain("temperature");
        payload.Should().Contain("tool_choice");
    }

    private static readonly object ChatDiagnosticsGate = new();

    [TestMethod]
    public void LogOutboundChatRequest_EmitsFullRequest_WhenDebugEnabled()
    {
        lock (ChatDiagnosticsGate)
        {
            var factory = new CapturingLoggerFactory(LogLevel.Debug);
            ChatDiagnostics.Initialize(factory);

            try
            {
                var request = new ChatCompletionRequest(
                    messages: [new ChatMessage(ChatRole.User, "visibility-probe-debug-xyz")],
                    model: "test-model");

                Invoke<object?>("LogOutboundChatRequest", 3, request);

                var debug = factory.Entries
                    .Where(e => e.Category == ThreadRun.DiagnosticsCategory && e.Level == LogLevel.Debug)
                    .Select(e => e.Message)
                    .ToList();
                debug.Should().ContainSingle(m =>
                    m.Contains("ThreadRun outbound chat request.", StringComparison.Ordinal)
                    && m.Contains("Round=3", StringComparison.Ordinal)
                    && m.Contains("visibility-probe-debug-xyz", StringComparison.Ordinal));
            }
            finally
            {
                ChatDiagnostics.Initialize(NullLoggerFactory.Instance);
            }
        }
    }

    [TestMethod]
    public void LogOutboundChatRequest_IsSilent_WhenOnlyInformationEnabled()
    {
        lock (ChatDiagnosticsGate)
        {
            var factory = new CapturingLoggerFactory(LogLevel.Information);
            ChatDiagnostics.Initialize(factory);

            try
            {
                var request = new ChatCompletionRequest(
                    messages: [new ChatMessage(ChatRole.User, "visibility-probe-info-xyz")],
                    model: "test-model");

                Invoke<object?>("LogOutboundChatRequest", 1, request);

                factory.Entries.Should().BeEmpty();
            }
            finally
            {
                ChatDiagnostics.Initialize(NullLoggerFactory.Instance);
            }
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly LogLevel _minimumLevel;

        public CapturingLoggerFactory(LogLevel minimumLevel)
        {
            _minimumLevel = minimumLevel;
        }

        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, _minimumLevel, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly LogLevel _minimumLevel;
            private readonly List<(string Category, LogLevel Level, string Message)> _entries;

            public CapturingLogger(
                string category,
                LogLevel minimumLevel,
                List<(string Category, LogLevel Level, string Message)> entries)
            {
                _category = category;
                _minimumLevel = minimumLevel;
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                _entries.Add((_category, logLevel, formatter(state, exception)));
            }
        }
    }

    [TestMethod]
    public void ClearRequestBuilderCache_AndClearAll_DoNotThrow()
    {
        Action clearOne = () => ThreadRun.ClearRequestBuilderCache("non-existent-assistant");
        Action clearAll = ThreadRun.ClearAllRequestBuilderCache;

        clearOne.Should().NotThrow();
        clearAll.Should().NotThrow();
        clearOne.Should().NotThrow();
    }

    [TestMethod]
    public void MergeRoundUsage_AccumulatesAcrossRounds()
    {
        var first = Invoke<UsageResponse>(
            "MergeRoundUsage",
            null,
            new ChatCompletionUsage
            {
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150,
                PromptTokensDetails = new ChatPromptTokensDetails { CachedTokens = 10 }
            });

        first.PromptTokens.Should().Be(100);
        first.CompletionTokens.Should().Be(50);
        first.CachedPromptTokens.Should().Be(10);
        first.TotalTokens.Should().Be(150);

        var combined = Invoke<UsageResponse>(
            "MergeRoundUsage",
            first,
            new ChatCompletionUsage
            {
                PromptTokens = 200,
                CompletionTokens = 75,
                TotalTokens = 275,
                PromptTokensDetails = new ChatPromptTokensDetails { CachedTokens = 5 }
            });

        combined.PromptTokens.Should().Be(300);
        combined.CompletionTokens.Should().Be(125);
        combined.CachedPromptTokens.Should().Be(15);
        combined.TotalTokens.Should().Be(425);
    }

    [TestMethod]
    public void InjectLimitToolResults_AddsSyntheticToolMessagePerCall()
    {
        var limitState = new ToolLimitState(1, 1, LimitEscalationPhase.SoftBlocked);
        var messages = new List<ChatMessage>();
        var toolCalls = new List<ChatToolCall>
        {
            new()
            {
                Id = "call_limit",
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = "search",
                    Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { q = "x" })
                }
            }
        };

        Invoke<Task>("InjectLimitToolResultsAsync", toolCalls, messages, limitState, 1, null)
            .GetAwaiter()
            .GetResult();

        messages.Should().ContainSingle(m => m.Role == ChatRole.Tool);
        messages[0].GetText().Should().Contain("Tool execution limit reached");
    }

    [TestMethod]
    public void EnsureLimitReachedSystemNudge_AddsSystemMessageOnce()
    {
        var messages = new List<ChatMessage>();
        var limitState = new ToolLimitState(5, 5, LimitEscalationPhase.None);

        Invoke<object>("EnsureLimitReachedSystemNudge", messages, limitState, null);
        Invoke<object>("EnsureLimitReachedSystemNudge", messages, limitState, null);

        messages.Should().ContainSingle(m =>
            m.Role == ChatRole.System &&
            m.GetText().Contains("was reached for this turn", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ForceCompleteOnToolLimit_AddsAssistantSummaryMessage()
    {
        var messages = new List<ChatMessage>();

        Invoke<object>("ForceCompleteOnToolLimit", messages, null);

        messages.Should().ContainSingle(m =>
            m.Role == ChatRole.Assistant &&
            m.GetText().Contains("tool execution limit", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BuildCompactedHistoryForLimitSummary_OmitsToolAndToolCallAssistantMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "question"),
            new(ChatRole.Assistant, [new ChatContent("calling")], toolCalls:
            [
                new ChatToolCall
                {
                    Id = "c1",
                    Type = "function",
                    Function = new ChatToolCallFunction { Name = "search", Arguments = default }
                }
            ]),
            new ChatMessage("c1", "search", [new ChatContent("result")]),
            new(ChatRole.Assistant, "answer")
        };

        var compacted = Invoke<List<ChatMessage>>("BuildCompactedHistoryForLimitSummary", messages);

        compacted.Should().HaveCount(2);
        compacted[0].Role.Should().Be(ChatRole.User);
        compacted[1].GetText().Should().Be("answer");
    }
}
