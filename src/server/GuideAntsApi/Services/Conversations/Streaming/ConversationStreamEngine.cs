using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using GuideAnts.Usage;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Tracing;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class ConversationStreamEngine : IConversationStreamEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChatCompletionClientFactory _chatClientFactory;
    private readonly IConversationPersistence _persistence;
    private readonly IConversationUsageReporter _usageReporter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotebookFileSyncService? _notebookFileSyncService;
    private readonly ILogger<ConversationStreamEngine> _logger;

    public ConversationStreamEngine(
        IHttpClientFactory httpClientFactory,
        IChatCompletionClientFactory chatClientFactory,
        IConversationPersistence persistence,
        IConversationUsageReporter usageReporter,
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationStreamEngine> logger,
        INotebookFileSyncService? notebookFileSyncService = null)
    {
        _httpClientFactory = httpClientFactory;
        _chatClientFactory = chatClientFactory;
        _persistence = persistence;
        _usageReporter = usageReporter;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notebookFileSyncService = notebookFileSyncService;
    }

    public async IAsyncEnumerable<StreamingEvent> RunStreamAsync(
        ConversationStreamRunContext context,
        IStreamLockHandle lockHandle,
        [EnumeratorCancellation] CancellationToken externalCt)
    {
        var policy = context.Policy;
        var channel = CreateChannel();

        await policy.OnStreamingStartedAsync(
            context.ConversationId,
            new StreamStreamingStartedInfo(context.AssistantName, context.TurnIndex),
            CancellationToken.None);

        StartBackgroundRun(context, channel.Writer, externalCt);

        var streamingSucceeded = false;
        var sawPendingClientTool = false;
        var sawFailureEvent = false;
        var distributedLockReleased = false;

        async Task ReleaseStreamLockIfHeldAsync()
        {
            if (distributedLockReleased)
            {
                return;
            }

            distributedLockReleased = await lockHandle.ReleaseAsync(CancellationToken.None);
            if (lockHandle.ConversationLockEventSent && distributedLockReleased)
            {
                try
                {
                    await policy.OnUnlockAsync(context.ConversationId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to broadcast conversation unlock for {ConversationId}", context.ConversationId);
                }
            }
        }

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(externalCt))
            {
                if (ev.EventType == StreamingEventTypes.ExternalToolCall && policy.SupportsExternalToolResume)
                {
                    // guideants-chat resumes on external_tool_call (before pending_client_tool). Release
                    // here so a parallel tool-calls/results?resume=true can acquire the lock.
                    await ReleaseStreamLockIfHeldAsync();
                }
                else if (ev.EventType == StreamingEventTypes.PendingClientTool)
                {
                    sawPendingClientTool = true;
                    // Belt-and-suspenders for clients that wait for pending_client_tool.
                    await ReleaseStreamLockIfHeldAsync();
                }
                else if (ev.EventType == StreamingEventTypes.Cancelled || ev.EventType == StreamingEventTypes.Error)
                {
                    sawFailureEvent = true;
                }

                await policy.BroadcastEventAsync(context.ConversationId, ev, externalCt);
                yield return ev;
            }

            // The background run owns terminal turn status (completed / cancelled / pending). A clean
            // channel drain with no terminal event therefore means a successful turn.
            streamingSucceeded = !sawPendingClientTool && !sawFailureEvent;
        }
        finally
        {
            if (!streamingSucceeded && !sawPendingClientTool && context.DbTurn != null)
            {
                try
                {
                    await _persistence.SetTurnStatusAsync(
                        context.DbTurn.Id,
                        "cancelled",
                        onlyIfCurrentStatus: "streaming",
                        ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating turn status to cancelled for {ConversationId}", context.ConversationId);
                }
            }

            await ReleaseStreamLockIfHeldAsync();
        }

        if (streamingSucceeded && (distributedLockReleased || !lockHandle.ConversationLockEventSent))
        {
            await policy.OnCompleteAsync(context.ConversationId, CancellationToken.None);
            yield return new StreamingEvent(StreamingEventTypes.Complete, "{}");
        }
    }

    private static Channel<StreamingEvent> CreateChannel() =>
        Channel.CreateBounded<StreamingEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private void StartBackgroundRun(
        ConversationStreamRunContext context,
        ChannelWriter<StreamingEvent> writer,
        CancellationToken externalCt)
    {
        _ = Task.Run(async () =>
        {
            var policy = context.Policy;
            var noneCt = CancellationToken.None;
            SemaphoreSlim? throttler = policy.UsesProgressThrottling ? new SemaphoreSlim(50, 50) : null;

            Guid? currentAssistantMessageId = null;
            var currentAssistantContent = new StringBuilder();
            var currentMessageSequence = context.InitialMessageSequence;
            var filenameUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assistantMessageIds = new List<Guid>();
            var thinkingEmittedInStream = false;
            var flushCounter = 0;
            const int flushInterval = 20;
            var fileUrlContext = policy.BuildFileUrlContext(context.Conversation, context.PublisherId, context.HostUrl);
            var turnTraceCollector = new TurnTraceCollector(context.AssistantName, context.ModelDeploymentId);
            context.ChatOptions.TraceCollector = turnTraceCollector;

            async Task PersistTraceSegmentAsync(string captureState, string? errorMessage = null, CancellationToken ct = default)
            {
                var segment = turnTraceCollector.BuildFinalizedSegment(captureState, errorMessage);
                var segmentJson = JsonSerializer.Serialize(segment, JsonOptions);
                await _persistence.AppendTurnTraceSegmentAsync(
                    new AppendTurnTraceSegmentRequest(
                        context.DbTurn.Id,
                        context.Conversation.Id,
                        context.TurnIndex,
                        TurnTraceCollector.SchemaVersion,
                        captureState,
                        segmentJson),
                    ct);
            }

            void TryWrite(StreamingEvent ev)
            {
                if (externalCt.IsCancellationRequested)
                {
                    return;
                }

                if (throttler != null)
                {
                    if (throttler.Wait(100))
                    {
                        try
                        {
                            writer.TryWrite(ev);
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }

                    return;
                }

                try
                {
                    writer.TryWrite(ev);
                }
                catch
                {
                    // channel may be completed
                }
            }

            StreamingMessageProgressEventHandler onProgress = (_, e) =>
            {
                if (externalCt.IsCancellationRequested)
                {
                    return;
                }

                var isThinking = string.Equals(e.Role, "assistant_thinking", StringComparison.OrdinalIgnoreCase);
                if (isThinking)
                {
                    thinkingEmittedInStream = true;
                }

                if (currentAssistantMessageId == null)
                {
                    var msgId = _persistence.StartAssistantMessageAsync(
                        new StartAssistantMessageRequest(
                            context.Conversation.Id,
                            context.DbTurn.Id,
                            context.TurnIndex,
                            currentMessageSequence++,
                            context.AssistantName,
                            context.ModelDeploymentId,
                            context.AssistantId),
                        CancellationToken.None).GetAwaiter().GetResult();
                    currentAssistantMessageId = msgId;
                    assistantMessageIds.Add(msgId);
                }

                if (!isThinking)
                {
                    currentAssistantContent.Append(e.ContentDelta);
                    flushCounter++;

                    if (flushCounter % flushInterval == 0)
                    {
                        try
                        {
                            if (currentAssistantMessageId != null)
                            {
                                _persistence.AppendOrFinalizeAssistantMessageAsync(
                                    new AssistantMessageUpdateRequest(
                                        currentAssistantMessageId.Value,
                                        context.DbTurn.Id,
                                        currentAssistantContent.ToString(),
                                        Finalize: false),
                                    CancellationToken.None).GetAwaiter().GetResult();
                            }

                            if (!policy.SupportsExternalToolResume)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await policy.BroadcastStreamingProgressAsync(
                                            context.ConversationId,
                                            context.User,
                                            currentAssistantContent.Length,
                                            flushCounter,
                                            CancellationToken.None);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to broadcast streaming progress");
                                    }
                                });
                            }
                        }
                        catch
                        {
                            // logged on finalization
                        }
                    }
                }

                if (policy.SupportsExternalToolResume)
                {
                    var tokenPayload = new { role = "assistant", contentDelta = e.ContentDelta };
                    TryWrite(new StreamingEvent(StreamingEventTypes.Token, JsonSerializer.Serialize(tokenPayload, JsonOptions)));
                }
                else
                {
                    var payload = new { role = "assistant", contentDelta = e.ContentDelta, timestamp = DateTime.UtcNow };
                    TryWrite(new StreamingEvent(StreamingEventTypes.AssistantMessage, JsonSerializer.Serialize(payload, JsonOptions)));
                }
            };

            MessageAddedEventHandler onMessageAdded = (_, e) =>
            {
                if (externalCt.IsCancellationRequested || string.IsNullOrEmpty(e.Role))
                {
                    return;
                }

                if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    HandleAssistantMessageAdded(
                        e,
                        context,
                        policy,
                        fileUrlContext,
                        filenameUrlMap,
                        ref currentAssistantMessageId,
                        ref currentMessageSequence,
                        currentAssistantContent,
                        assistantMessageIds,
                        TryWrite);
                    return;
                }

                if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    HandleToolMessageAdded(
                        e,
                        context,
                        policy,
                        fileUrlContext,
                        filenameUrlMap,
                        ref currentMessageSequence,
                        TryWrite);
                }
            };

            try
            {
                externalCt.ThrowIfCancellationRequested();
                var httpClient = _httpClientFactory.CreateClient();
                ChatRunOutput? output;

                if (policy.SupportsExternalToolResume)
                {
                    var invocationContext = new AntRunner.ToolCalling.InvocationContext(
                        ProjectId: context.Conversation.Notebook.ProjectId,
                        NotebookId: context.Conversation.NotebookId,
                        ConversationId: context.Conversation.Id,
                        OAuthUserAccessToken: context.ChatOptions.oAuthUserAccessToken)
                    {
                        TurnIndex = context.TurnIndex,
                        AssistantId = context.AssistantId,
                        NotebookConversationMessageId = context.UserMessageId,
                        ToolActivitySink = activity =>
                        {
                            try
                            {
                                TryWrite(StreamingEvents.BuildToolActivityProgress(activity));
                            }
                            catch
                            {
                                // Activity metadata is best-effort; never interrupt tool execution.
                            }
                        }
                    };

                    output = await AntRunner.Chat.ThreadRun.ExecuteAsync(
                        context.ChatOptions,
                        _chatClientFactory,
                        context.PreviousMessages,
                        httpClient,
                        onMessageAdded,
                        onProgress,
                        onExternalToolCall: (_, evt) =>
                        {
                            try
                            {
                                var payload = new { toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(evt.ToolCallsJson, JsonOptions) };
                                TryWrite(new StreamingEvent(StreamingEventTypes.ExternalToolCall, JsonSerializer.Serialize(payload, JsonOptions)));
                            }
                            catch
                            {
                                TryWrite(new StreamingEvent(StreamingEventTypes.ExternalToolCall, evt.ToolCallsJson));
                            }
                        },
                        resumeWithoutNewUserMessage: context.ResumeWithoutNewUserMessage,
                        ctx: invocationContext,
                        token: externalCt,
                        isAgentInvocation: false);
                }
                else
                {
                    output = await ChatRunner.RunThread(
                        context.ChatOptions,
                        _chatClientFactory,
                        previousMessages: context.PreviousMessages,
                        httpClient: httpClient,
                        messageAdded: onMessageAdded,
                        streamingMessageProgress: onProgress,
                        projectId: context.Conversation.Notebook.ProjectId.ToString(),
                        notebookId: context.Conversation.NotebookId.ToString(),
                        conversationId: context.Conversation.Id.ToString(),
                        turnIndex: context.TurnIndex,
                        assistantId: context.AssistantId,
                        notebookConversationMessageId: context.UserMessageId,
                        toolActivitySink: activity =>
                        {
                            try
                            {
                                TryWrite(StreamingEvents.BuildToolActivityProgress(activity));
                            }
                            catch
                            {
                                // Activity metadata is best-effort; never interrupt tool execution.
                            }
                        },
                        cancellationToken: externalCt);
                }

                if (output?.Status != null
                    && output.Status.Equals("pending_client_tool", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentAssistantMessageId != null)
                    {
                        await _persistence.FinalizeStreamingAssistantMessageIfStillStreamingAsync(
                            currentAssistantMessageId.Value,
                            context.DbTurn.Id,
                            currentAssistantContent.ToString(),
                            noneCt);
                    }

                    await _persistence.PersistThinkingBlocksAsync(output, assistantMessageIds, noneCt);

                    await _persistence.SetTurnStatusAsync(context.DbTurn.Id, "streaming", ct: noneCt);
                    await PersistTraceSegmentAsync("partial", ct: noneCt);
                    TryWrite(new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"));
                    return;
                }

                if (currentAssistantMessageId != null)
                {
                    await _persistence.FinalizeStreamingAssistantMessageIfStillStreamingAsync(
                        currentAssistantMessageId.Value,
                        context.DbTurn.Id,
                        currentAssistantContent.ToString(),
                        noneCt);
                }

                await _persistence.PersistThinkingBlocksAsync(output, assistantMessageIds, noneCt);
                if (!thinkingEmittedInStream)
                {
                    StreamingEvents.EmitThinkingMessages(output, assistantMessageIds, writer);
                }

                await RecordToolUsageAsync(context, noneCt);

                if (output != null)
                {
                    await _persistence.SetTurnStatusAsync(context.DbTurn.Id, "completed", ct: noneCt);
                    await _persistence.PersistRunOutputAsync(context.DbTurn.Id, output, noneCt);

                    if (output.Usage != null)
                    {
                        await RecordChatUsageAsync(context, output, currentAssistantMessageId, assistantMessageIds, TryWrite, noneCt);
                    }
                }

                await RegisterAndQueueNotebookSyncIfNeededAsync(context, output);
                await PersistTraceSegmentAsync("completed", ct: noneCt);
            }
            catch (OperationCanceledException ex)
            {
                try
                {
                    var partialOutput = (ex as ChatRunCancelledException)?.ChatRunOutput;
                    await HandleCancellationAsync(
                        context,
                        currentAssistantMessageId,
                        currentAssistantContent,
                        assistantMessageIds,
                        TryWrite,
                        partialOutput,
                        noneCt);
                    await PersistTraceSegmentAsync("cancelled", ct: noneCt);
                }
                catch (Exception cancellationHandlingException)
                {
                    _logger.LogError(
                        cancellationHandlingException,
                        "Failed while handling cancellation for {ConversationId} turn {TurnIndex}",
                        context.Conversation.Id,
                        context.TurnIndex);

                    TryWrite(new StreamingEvent(
                        StreamingEventTypes.Error,
                        JsonSerializer.Serialize(StreamingErrorEnvelope.Build(cancellationHandlingException), JsonOptions)));
                }
            }
            catch (Exception ex)
            {
                Exception surfacedException = ex;
                try
                {
                    await PersistTraceSegmentAsync("failed", ex.Message, noneCt);
                }
                catch (Exception tracePersistException)
                {
                    surfacedException = new InvalidOperationException(
                        "Conversation failed and prompt-trace persistence also failed.",
                        new AggregateException(ex, tracePersistException));
                    _logger.LogError(
                        tracePersistException,
                        "Prompt-trace persistence failed for {ConversationId} turn {TurnIndex}",
                        context.Conversation.Id,
                        context.TurnIndex);
                }

                _logger.LogError(
                    surfacedException,
                    "Streaming conversation failed for {ConversationId} turn {TurnIndex}",
                    context.Conversation.Id,
                    context.TurnIndex);

                if (currentAssistantMessageId != null)
                {
                    await _persistence.AppendOrFinalizeAssistantMessageAsync(
                        new AssistantMessageUpdateRequest(
                            currentAssistantMessageId.Value,
                            context.DbTurn.Id,
                            currentAssistantContent.ToString(),
                            Finalize: true),
                        noneCt);
                }

                TryWrite(new StreamingEvent(
                    StreamingEventTypes.Error,
                    JsonSerializer.Serialize(StreamingErrorEnvelope.Build(surfacedException), JsonOptions)));
            }
            finally
            {
                throttler?.Dispose();
                writer.Complete();
            }
        }, externalCt);
    }

    private void HandleAssistantMessageAdded(
        MessageAddedEventArgs e,
        ConversationStreamRunContext context,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        ref Guid? currentAssistantMessageId,
        ref int currentMessageSequence,
        StringBuilder currentAssistantContent,
        List<Guid> assistantMessageIds,
        Action<StreamingEvent> tryWrite)
    {
        if (!string.IsNullOrEmpty(e.ToolCallsJson))
        {
            var toolCallAssistantText = policy.SanitizeAssistantContent(e.Message ?? string.Empty, filenameUrlMap, fileUrlContext);
            var toolCallsJson = e.ToolCallsJson;
            List<ChatToolCall>? toolCallsForDb = null;
            try
            {
                toolCallsForDb = JsonSerializer.Deserialize<List<ChatToolCall>>(toolCallsJson!, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to deserialize tool calls for conversation {ConversationId} turn {TurnIndex}",
                    context.Conversation.Id,
                    context.TurnIndex);
            }

            if (currentAssistantMessageId != null)
            {
                _persistence.AppendOrFinalizeAssistantMessageAsync(
                    new AssistantMessageUpdateRequest(
                        currentAssistantMessageId.Value,
                        context.DbTurn.Id,
                        toolCallAssistantText,
                        Finalize: true,
                        ToolCallsJson: toolCallsJson),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                var toolCallMessageId = _persistence.StartAssistantMessageAsync(
                    new StartAssistantMessageRequest(
                        context.Conversation.Id,
                        context.DbTurn.Id,
                        context.TurnIndex,
                        currentMessageSequence++,
                        context.AssistantName,
                        context.ModelDeploymentId,
                        context.AssistantId,
                        Content: toolCallAssistantText,
                        IsStreaming: false,
                        ToolCallsJson: toolCallsJson),
                    CancellationToken.None).GetAwaiter().GetResult();
                assistantMessageIds.Add(toolCallMessageId);
            }

            if (!policy.SupportsExternalToolResume)
            {
                try
                {
                    var toolCallsForClient = toolCallsForDb?.Select(tc => new
                    {
                        id = tc.Id,
                        type = tc.Type.ToString().ToLowerInvariant(),
                        function = new
                        {
                            name = tc.Function.Name,
                            arguments = tc.Function.Arguments.ToString()
                        }
                    }).ToList();

                    var assistantToolCallPayload = new
                    {
                        role = "assistant",
                        content = string.Empty,
                        tool_calls = toolCallsForClient,
                        timestamp = DateTime.UtcNow
                    };
                    tryWrite(new StreamingEvent(
                        StreamingEventTypes.AssistantMessage,
                        JsonSerializer.Serialize(assistantToolCallPayload, JsonOptions)));
                }
                catch
                {
                    // non-fatal
                }
            }

            currentAssistantMessageId = null;
            currentAssistantContent.Clear();

            if (policy.SupportsExternalToolResume)
            {
                EmitStreamMessage(e.Role!, e.Message ?? string.Empty, policy, fileUrlContext, filenameUrlMap, tryWrite);
            }
            else
            {
                tryWrite(BuildAssistantStreamEvent(e.Message ?? string.Empty, policy, fileUrlContext, filenameUrlMap));
            }

            return;
        }

        if (currentAssistantMessageId == null)
        {
            var sanitized = policy.SanitizeAssistantContent(e.Message ?? string.Empty, filenameUrlMap, fileUrlContext);
            var contentMessageId = _persistence.StartAssistantMessageAsync(
                new StartAssistantMessageRequest(
                    context.Conversation.Id,
                    context.DbTurn.Id,
                    context.TurnIndex,
                    currentMessageSequence++,
                    context.AssistantName,
                    context.ModelDeploymentId,
                    context.AssistantId,
                    Content: sanitized,
                    IsStreaming: false),
                CancellationToken.None).GetAwaiter().GetResult();
            assistantMessageIds.Add(contentMessageId);
            tryWrite(BuildAssistantStreamEvent(sanitized, policy, fileUrlContext, filenameUrlMap));
            return;
        }

        var finalized = policy.SanitizeAssistantContent(e.Message ?? string.Empty, filenameUrlMap, fileUrlContext);
        _persistence.AppendOrFinalizeAssistantMessageAsync(
            new AssistantMessageUpdateRequest(
                currentAssistantMessageId.Value,
                context.DbTurn.Id,
                finalized,
                Finalize: true,
                ToolCallsJson: string.IsNullOrEmpty(e.ToolCallsJson) ? null : e.ToolCallsJson),
            CancellationToken.None).GetAwaiter().GetResult();
        currentAssistantMessageId = null;
        currentAssistantContent.Clear();
        tryWrite(BuildAssistantStreamEvent(finalized, policy, fileUrlContext, filenameUrlMap));
    }

    private void HandleToolMessageAdded(
        MessageAddedEventArgs e,
        ConversationStreamRunContext context,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        ref int currentMessageSequence,
        Action<StreamingEvent> tryWrite)
    {
        var sanitizedContent = policy.SanitizeToolContent(e.Message ?? string.Empty, fileUrlContext);

        try
        {
            var result = _persistence.CreateToolMessageAsync(
                new CreateToolMessageRequest(
                    context.Conversation.Id,
                    context.DbTurn.Id,
                    context.TurnIndex,
                    currentMessageSequence,
                    sanitizedContent,
                    e.ToolCallId,
                    e.FunctionName,
                    context.AssistantId,
                    context.AssistantName),
                CancellationToken.None).GetAwaiter().GetResult();

            // Replacements (e.g. context-overflow unwind) update in place and must not consume a sequence.
            if (result.Created)
            {
                currentMessageSequence++;
            }

            policy.UpdateFilenameUrlMapFromToolMessage(
                sanitizedContent,
                fileUrlContext,
                filenameUrlMap,
                context.Conversation);
        }
        catch
        {
            // logged on finalization
        }

        if (policy.SupportsExternalToolResume)
        {
            EmitStreamMessage(e.Role!, sanitizedContent, policy, fileUrlContext, filenameUrlMap, tryWrite);
        }
        else
        {
            var toolPayload = new
            {
                role = "tool",
                content = sanitizedContent,
                toolCallId = e.ToolCallId,
                functionName = e.FunctionName,
                arguments = e.ToolCallsJson,
                timestamp = DateTime.UtcNow
            };
            tryWrite(new StreamingEvent(StreamingEventTypes.ToolResult, JsonSerializer.Serialize(toolPayload, JsonOptions)));
        }
    }

    private static void EmitStreamMessage(
        string role,
        string message,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        Action<StreamingEvent> tryWrite)
    {
        var eventType = role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? StreamingEventTypes.Message
            : StreamingEvents.DetermineEventType(role, message);

        string payloadContent;
        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            payloadContent = policy.SanitizeAssistantContent(message, filenameUrlMap, fileUrlContext);
        }
        else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            payloadContent = policy.SanitizeToolContent(message, fileUrlContext);
        }
        else
        {
            payloadContent = message;
        }

        var payload = new { role = role.ToLowerInvariant(), content = payloadContent, timestamp = DateTime.UtcNow };
        tryWrite(new StreamingEvent(eventType, JsonSerializer.Serialize(payload, JsonOptions)));
    }

    private static StreamingEvent BuildAssistantStreamEvent(
        string content,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap)
    {
        var eventType = policy.SupportsExternalToolResume
            ? StreamingEventTypes.Message
            : StreamingEventTypes.AssistantMessage;
        var payload = new { role = "assistant", content, timestamp = DateTime.UtcNow };
        return new StreamingEvent(eventType, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private async Task RecordToolUsageAsync(ConversationStreamRunContext context, CancellationToken ct)
    {
        await _usageReporter.RecordToolCallUsageForTurnAsync(
            new ToolTurnUsageRequest(
                context.Policy.UsageMode,
                context.Conversation.Notebook.ProjectId,
                context.Conversation.NotebookId,
                context.Conversation.Id,
                context.TurnIndex,
                context.AssistantId,
                ContextLabel: context.UsageContextLabel),
            ct);
    }

    private async Task RecordChatUsageAsync(
        ConversationStreamRunContext context,
        ChatRunOutput output,
        Guid? currentAssistantMessageId,
        IReadOnlyList<Guid> assistantMessageIds,
        Action<StreamingEvent> tryWrite,
        CancellationToken ct)
    {
        var usagePayload = new
        {
            promptTokens = output.Usage!.PromptTokens,
            completionTokens = output.Usage.CompletionTokens,
            totalTokens = output.Usage.TotalTokens
        };
        tryWrite(new StreamingEvent(StreamingEventTypes.Usage, JsonSerializer.Serialize(usagePayload, JsonOptions)));

        var cached = output.Usage.CachedPromptTokens ?? 0;
        var prompt = output.Usage.PromptTokens ?? 0;
        var completion = output.Usage.CompletionTokens ?? 0;
        var metrics = new UsageMetrics(prompt, cached, 0, completion);

        await _usageReporter.RecordChatCompletionUsageAsync(
            new ChatCompletionUsageRequest(
                context.Policy.UsageMode,
                context.Conversation.Notebook.ProjectId,
                context.Conversation.NotebookId,
                context.Conversation.Id,
                context.TurnIndex,
                context.ModelDeploymentId,
                context.AssistantId,
                metrics,
                PreferredAssistantMessageId: currentAssistantMessageId
                    ?? (assistantMessageIds.Count > 0 ? assistantMessageIds[^1] : null),
                AssistantMessageIds: assistantMessageIds),
            ct);
    }

    private async Task RegisterAndQueueNotebookSyncIfNeededAsync(ConversationStreamRunContext context, ChatRunOutput? output)
    {
        var turnReportedFileChanges =
            (output?.NewFiles?.Count > 0) ||
            (output?.ModifiedFiles?.Count > 0);

        if (_notebookFileSyncService == null || context.Conversation.Notebook == null || !turnReportedFileChanges)
        {
            return;
        }

        try
        {
            var isPublished = context.Policy.UsageMode != ConversationUsageMode.Private;
            var runId = NotebookPathResolver.TryExtractRunIdFromWorkingDirectory(context.DbTurn.WorkingDirectory);
            var dbPaths = NotebookFileChangeReporter.GetDbRelativePaths(output, isPublished, runId);

            if (dbPaths.Count > 0)
            {
                await _notebookFileSyncService.RegisterFilesAsync(
                    context.Conversation.Notebook.Id,
                    dbPaths,
                    CancellationToken.None);
            }

            await _notebookFileSyncService.QueueReconcileAsync(context.Conversation.Notebook.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register/queue notebook sync after turn completion");
        }
    }

    private async Task HandleCancellationAsync(
        ConversationStreamRunContext context,
        Guid? currentAssistantMessageId,
        StringBuilder currentAssistantContent,
        IReadOnlyList<Guid> assistantMessageIds,
        Action<StreamingEvent> tryWrite,
        ChatRunOutput? partialOutput,
        CancellationToken ct)
    {
        if (currentAssistantMessageId != null)
        {
            await _persistence.AppendOrFinalizeAssistantMessageAsync(
                new AssistantMessageUpdateRequest(
                    currentAssistantMessageId.Value,
                    context.DbTurn.Id,
                    currentAssistantContent.ToString(),
                    Finalize: true),
                ct);
        }

        if (!context.Policy.SupportsExternalToolResume)
        {
            try
            {
                await _persistence.PruneIncompleteToolCallsAsync(context.Conversation.Id, context.TurnIndex, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to prune incomplete tool calls for conversation {ConversationId} turn {TurnIndex}",
                    context.Conversation.Id,
                    context.TurnIndex);
            }
        }
        else
        {
            try
            {
                await _persistence.SetTurnStatusAsync(context.DbTurn.Id, "cancelled", ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark published turn as cancelled");
            }
        }

        await RecordToolUsageAsync(context, ct);

        if (partialOutput?.Usage != null)
        {
            await RecordChatUsageAsync(
                context,
                partialOutput,
                currentAssistantMessageId,
                assistantMessageIds,
                tryWrite,
                ct);
        }
        else
        {
            await _usageReporter.RecordCancelledTurnMarkerUsageAsync(
                new CancelledTurnUsageRequest(
                    context.Policy.UsageMode,
                    context.Conversation.Notebook.ProjectId,
                    context.Conversation.NotebookId,
                    context.Conversation.Id,
                    context.TurnIndex,
                    context.ModelDeploymentId,
                    context.AssistantId,
                    PreferredAssistantMessageId: currentAssistantMessageId
                        ?? (assistantMessageIds.Count > 0 ? assistantMessageIds[^1] : null),
                    AssistantMessageIds: assistantMessageIds,
                    ContextLabel: context.UsageContextLabel != null ? $"{context.UsageContextLabel}(cancelled)" : null),
                ct);
        }

        var cancelPayload = new
        {
            message = "Stream was cancelled by user",
            type = "Cancellation",
            timestamp = DateTime.UtcNow,
            turnIndex = context.Policy.SupportsExternalToolResume ? (int?)null : context.TurnIndex
        };
        tryWrite(new StreamingEvent(StreamingEventTypes.Cancelled, JsonSerializer.Serialize(cancelPayload, JsonOptions)));
    }
}
