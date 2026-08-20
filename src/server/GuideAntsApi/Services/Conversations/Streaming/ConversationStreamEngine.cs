using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AntRunner.Chat;
using AntRunner.Chat.LlamaCpp;
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
        [EnumeratorCancellation] CancellationToken sseCt,
        CancellationToken workerCt,
        Action? onWorkerCompleted = null)
    {
        var policy = context.Policy;
        var channel = CreateChannel();
        var lockReleased = 0;

        async Task ReleaseStreamLockIfHeldAsync()
        {
            if (Interlocked.CompareExchange(ref lockReleased, 1, 0) != 0)
            {
                return;
            }

            var distributedLockReleased = await lockHandle.ReleaseAsync(CancellationToken.None);
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

        await policy.OnStreamingStartedAsync(
            context.ConversationId,
            new StreamStreamingStartedInfo(context.AssistantName, context.TurnIndex),
            CancellationToken.None);

        StartBackgroundRun(
            context,
            channel.Writer,
            sseCt,
            workerCt,
            ReleaseStreamLockIfHeldAsync,
            onWorkerCompleted);

        await foreach (var ev in channel.Reader.ReadAllAsync(sseCt))
        {
            yield return ev;
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
        CancellationToken sseCt,
        CancellationToken workerCt,
        Func<Task> releaseStreamLockAsync,
        Action? onWorkerCompleted)
    {
        _ = Task.Run(async () =>
        {
            var policy = context.Policy;
            var noneCt = CancellationToken.None;
            var streamingSucceeded = false;
            SemaphoreSlim? throttler = policy.UsesProgressThrottling ? new SemaphoreSlim(50, 50) : null;
            var hubWork = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            var hubPump = Task.Run(async () =>
            {
                await foreach (var work in hubWork.Reader.ReadAllAsync())
                {
                    try
                    {
                        await work().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast stream event for {ConversationId}", context.ConversationId);
                    }
                }
            });

            Guid? currentAssistantMessageId = null;
            var currentAssistantContent = new StringBuilder();
            var currentThinkingContent = new StringBuilder();
            var currentMessageSequence = context.InitialMessageSequence;
            var filenameUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assistantMessageIds = new List<Guid>();
            var thinkingEmittedInStream = false;
            var progressCheckpoint = new StreamingAssistantProgressCheckpoint(flushInterval: 20);
            var checkpointVersion = 0;
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
                if (!hubWork.Writer.TryWrite(() => policy.BroadcastEventAsync(context.ConversationId, ev, CancellationToken.None)))
                {
                    _logger.LogWarning(
                        "Dropped hub event {EventType} for {ConversationId}",
                        ev.EventType,
                        context.ConversationId);
                }

                if (ev.EventType == StreamingEventTypes.ExternalToolCall && policy.SupportsExternalToolResume)
                {
                    _ = releaseStreamLockAsync();
                }
                else if (ev.EventType == StreamingEventTypes.PendingClientTool)
                {
                    _ = releaseStreamLockAsync();
                }

                if (sseCt.IsCancellationRequested)
                {
                    return;
                }

                if (ConversationStreamEventWriter.IsTerminal(ev.EventType))
                {
                    ConversationStreamEventWriter.WriteTerminal(writer, ev, TimeSpan.FromSeconds(2));
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
                if (workerCt.IsCancellationRequested)
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

                if (isThinking)
                {
                    currentThinkingContent.Append(e.ContentDelta);
                }
                else
                {
                    currentAssistantContent.Append(e.ContentDelta);
                }

                if (progressCheckpoint.ShouldCheckpoint() && currentAssistantMessageId != null)
                {
                    try
                    {
                        checkpointVersion++;
                        string? thinkingJson = currentThinkingContent.Length > 0
                            ? JsonSerializer.Serialize(
                                new[] { ChatThinkingBlock.ForThinking(currentThinkingContent.ToString(), string.Empty) },
                                JsonOptions)
                            : null;
                        _persistence.CheckpointTurnAsync(
                            context.DbTurn.Id,
                            currentAssistantMessageId.Value,
                            currentAssistantContent.ToString(),
                            thinkingJson,
                            checkpointVersion,
                            CancellationToken.None).GetAwaiter().GetResult();

                        if (!isThinking && !policy.SupportsExternalToolResume)
                        {
                            if (!hubWork.Writer.TryWrite(() => policy.BroadcastStreamingProgressAsync(
                                    context.ConversationId,
                                    context.User,
                                    currentAssistantContent.Length,
                                    progressCheckpoint.FlushCounter,
                                    CancellationToken.None)))
                            {
                                _logger.LogWarning("Dropped streaming progress broadcast for {ConversationId}", context.ConversationId);
                            }
                        }
                    }
                    catch
                    {
                        // logged on finalization
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
                if (workerCt.IsCancellationRequested || string.IsNullOrEmpty(e.Role))
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
                    currentThinkingContent.Clear();
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
                workerCt.ThrowIfCancellationRequested();
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
                        token: workerCt,
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
                        cancellationToken: workerCt);
                }

                if (output?.Status != null
                    && output.Status.Equals("pending_client_tool", StringComparison.OrdinalIgnoreCase))
                {
                    await _persistence.TerminalizeTurnAsync(
                        ConversationTurnTerminalizer.BuildRequest(
                            context,
                            "pending_client_tool",
                            output,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds),
                        noneCt);
                    await PersistTraceSegmentAsync("partial", ct: noneCt);
                    TryWrite(new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"));
                    return;
                }

                if (!thinkingEmittedInStream)
                {
                    StreamingEvents.EmitThinkingMessages(output, assistantMessageIds, writer);
                }

                await RecordToolUsageAsync(context, noneCt);

                if (output != null)
                {
                    await _persistence.TerminalizeTurnAsync(
                        ConversationTurnTerminalizer.BuildRequest(
                            context,
                            "completed",
                            output,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds),
                        noneCt);

                    if (output.Usage != null)
                    {
                        await RecordChatUsageAsync(context, output, currentAssistantMessageId, assistantMessageIds, TryWrite, noneCt);
                    }
                }

                await RegisterAndQueueNotebookSyncIfNeededAsync(context, output);
                await PersistTraceSegmentAsync("completed", ct: noneCt);
                streamingSucceeded = true;
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
                        currentThinkingContent,
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
                ChatRunOutput? partialOutput = ex is ChatConversationException chatEx ? chatEx.ChatRunOutput : null;
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

                try
                {
                    var terminalStatus = ConversationTurnTerminalizer.MapTerminalStatus(partialOutput, ex);
                    await _persistence.TerminalizeTurnAsync(
                        ConversationTurnTerminalizer.BuildRequest(
                            context,
                            terminalStatus,
                            partialOutput,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds,
                            terminationCode: ConversationTurnTerminalizer.MapTerminationCode(ex),
                            terminationDetail: surfacedException.Message,
                            pruneIncompleteToolCalls: !context.Policy.SupportsExternalToolResume),
                        noneCt);

                    await RecordToolUsageAsync(context, noneCt);

                    if (partialOutput?.Usage != null)
                    {
                        await RecordChatUsageAsync(
                            context,
                            partialOutput,
                            currentAssistantMessageId,
                            assistantMessageIds,
                            TryWrite,
                            noneCt);
                    }
                }
                catch (Exception terminalizeException)
                {
                    _logger.LogError(
                        terminalizeException,
                        "Failed to terminalize turn after error for {ConversationId} turn {TurnIndex}",
                        context.Conversation.Id,
                        context.TurnIndex);
                }

                _logger.LogError(
                    surfacedException,
                    "Streaming conversation failed for {ConversationId} turn {TurnIndex}",
                    context.Conversation.Id,
                    context.TurnIndex);

                TryWrite(new StreamingEvent(
                    StreamingEventTypes.Error,
                    JsonSerializer.Serialize(StreamingErrorEnvelope.Build(surfacedException), JsonOptions)));
            }
            finally
            {
                try
                {
                    if (streamingSucceeded)
                    {
                        await policy.OnCompleteAsync(context.ConversationId, CancellationToken.None);
                        TryWrite(new StreamingEvent(StreamingEventTypes.Complete, "{}"));
                    }

                    await releaseStreamLockAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to finalize stream lifecycle for {ConversationId}", context.ConversationId);
                }

                throttler?.Dispose();
                hubWork.Writer.TryComplete();
                try
                {
                    await hubPump.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hub broadcast pump failed for {ConversationId}", context.ConversationId);
                }

                writer.TryComplete();
                onWorkerCompleted?.Invoke();
            }
        }, CancellationToken.None);
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
        StringBuilder currentThinkingContent,
        IReadOnlyList<Guid> assistantMessageIds,
        Action<StreamingEvent> tryWrite,
        ChatRunOutput? partialOutput,
        CancellationToken ct)
    {
        await _persistence.TerminalizeTurnAsync(
            ConversationTurnTerminalizer.BuildRequest(
                context,
                "cancelled",
                partialOutput,
                currentAssistantMessageId,
                currentAssistantContent,
                currentThinkingContent,
                assistantMessageIds,
                terminationCode: "cancelled",
                terminationDetail: "Stream was cancelled by user",
                pruneIncompleteToolCalls: !context.Policy.SupportsExternalToolResume),
            ct);

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

        var cancelPayload = new
        {
            message = "Stream was cancelled by user",
            type = "Cancellation",
            timestamp = DateTime.UtcNow,
            turnId = context.DbTurn.Id,
            status = "cancelled",
            terminationCode = "cancelled",
            turnIndex = context.Policy.SupportsExternalToolResume ? (int?)null : context.TurnIndex
        };
        tryWrite(new StreamingEvent(StreamingEventTypes.Cancelled, JsonSerializer.Serialize(cancelPayload, JsonOptions)));
    }
}
