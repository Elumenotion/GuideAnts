using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AntRunner.Chat;
using AntRunner.Chat.LlamaCpp;
using AntRunner.Chat.Abstractions;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Tracing;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class ConversationStreamEngine : IConversationStreamEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly TimeSpan LifecycleBestEffortTimeout = TimeSpan.FromSeconds(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChatCompletionClientFactory _chatClientFactory;
    private readonly IConversationPersistence _persistence;
    private readonly IConversationUsageReporter _usageReporter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationStreamRunRegistry _streamRunRegistry;
    private readonly INotebookFileSyncService? _notebookFileSyncService;
    private readonly ILogger<ConversationStreamEngine> _logger;

    public ConversationStreamEngine(
        IHttpClientFactory httpClientFactory,
        IChatCompletionClientFactory chatClientFactory,
        IConversationPersistence persistence,
        IConversationUsageReporter usageReporter,
        IServiceScopeFactory scopeFactory,
        ConversationStreamRunRegistry streamRunRegistry,
        ILogger<ConversationStreamEngine> logger,
        INotebookFileSyncService? notebookFileSyncService = null)
    {
        _httpClientFactory = httpClientFactory;
        _chatClientFactory = chatClientFactory;
        _persistence = persistence;
        _usageReporter = usageReporter;
        _scopeFactory = scopeFactory;
        _streamRunRegistry = streamRunRegistry;
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

            for (var releaseAttempt = 1; releaseAttempt <= 4; releaseAttempt++)
            {
                Task<bool>? releaseTask = null;
                try
                {
                    releaseTask = lockHandle.ReleaseAsync(CancellationToken.None);
                    if (await releaseTask.WaitAsync(LifecycleBestEffortTimeout).ConfigureAwait(false))
                    {
                        if (lockHandle.ConversationLockEventSent)
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

                        return;
                    }
                }
                catch (TimeoutException)
                {
                    if (releaseTask != null)
                    {
                        _ = ObserveTaskAsync(releaseTask);
                    }

                    _logger.LogWarning(
                        "Timed out releasing conversation lock for {ConversationId}; the lease will expire and recovery will repair it",
                        context.ConversationId);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Conversation lock release attempt {Attempt} failed for {ConversationId}",
                        releaseAttempt,
                        context.ConversationId);
                }

                if (releaseAttempt == 4)
                {
                    _logger.LogWarning(
                        "Conversation lock release is not confirmed for {ConversationId}; the lease will expire and recovery will repair it",
                        context.ConversationId);
                }

                if (releaseAttempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                }
            }
        }

        try
        {
            await policy.OnStreamingStartedAsync(
                context.ConversationId,
                new StreamStreamingStartedInfo(context.AssistantName, context.TurnIndex),
                CancellationToken.None);

            // Renew only while the inference worker runs. Renewing from lock acquire
            // (during history/setup) keeps ExpiresAt forever if setup hangs — Stop cannot clear it.
            lockHandle.BeginStreamingRenewal();

            StartBackgroundRun(
                context,
                channel.Writer,
                sseCt,
                workerCt,
                ReleaseStreamLockIfHeldAsync,
                onWorkerCompleted);
        }
        catch
        {
            await ReleaseStreamLockIfHeldAsync();
            onWorkerCompleted?.Invoke();
            throw;
        }

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
            var terminalizationConfirmed = false;
            var terminalizationAttempted = false;
            StreamingEvent? cancellationEvent = null;
            StreamingEvent? terminalEvent = null;
            StreamingEvent? deferredExternalToolCallEvent = null;
            var workerCompletionNotified = false;
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
            ChatRunOutput? output = null;
            var toolMessagePersistenceFailed = false;
            var thinkingEmittedInStream = false;
            // A delta is not acknowledged to the client until its content checkpoint succeeds.
            // StreamFlushInterval balances Stop durability against checkpoint write volume.
            var progressCheckpoint = new StreamingAssistantProgressCheckpoint();
            var checkpointVersion = context.DbTurn.CheckpointVersion;
            string? pendingAssistantToolCallsJson = null;
            string? pendingAssistantToolCallsContent = null;
            var executionWasFenced = false;
            Exception? checkpointPersistenceFailure = null;
            await using var checkpointQueue = new StreamingCheckpointPersistenceQueue(_persistence);

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
                        segmentJson,
                        context.DbTurn.ExecutionId),
                    ct);
            }

            void TryWrite(StreamingEvent ev)
            {
                if (workerCt.IsCancellationRequested
                    && !ConversationStreamEventWriter.IsTerminal(ev.EventType))
                {
                    return;
                }

                if (!hubWork.Writer.TryWrite(() => policy.BroadcastEventAsync(context.ConversationId, ev, CancellationToken.None)))
                {
                    _logger.LogWarning(
                        "Dropped hub event {EventType} for {ConversationId}",
                        ev.EventType,
                        context.ConversationId);
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

            void RefreshPersistenceState()
            {
                currentAssistantMessageId = checkpointQueue.ActiveMessageId;
                assistantMessageIds.Clear();
                assistantMessageIds.AddRange(checkpointQueue.AssistantMessageIds);
            }

            async Task FlushPersistenceQueueAsync(bool throwOnFailure)
            {
                try
                {
                    await checkpointQueue.FlushAsync(throwOnFailure: true).ConfigureAwait(false);
                    if (throwOnFailure && checkpointPersistenceFailure != null)
                    {
                        throw checkpointPersistenceFailure;
                    }
                }
                catch (ConversationTurnExecutionFencedException)
                {
                    executionWasFenced = true;
                    if (throwOnFailure)
                    {
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    checkpointPersistenceFailure ??= ex;
                    if (throwOnFailure)
                    {
                        throw;
                    }
                }
                finally
                {
                    RefreshPersistenceState();
                }
            }

            StreamingMessageProgressEventHandler onProgress = (_, e) =>
            {
                // A provider may flush buffered deltas while observing cancellation. Capture
                // those deltas for durable partial-response persistence, but do not publish
                // them as live progress after Stop was requested.
                checkpointQueue.ThrowIfFailed();
                var cancellationWasRequested = workerCt.IsCancellationRequested;
                var isThinking = string.Equals(e.Role, "assistant_thinking", StringComparison.OrdinalIgnoreCase);
                if (isThinking)
                {
                    thinkingEmittedInStream = true;
                }

                var segment = checkpointQueue.EnsureAssistantMessage(
                    new StartAssistantMessageRequest(
                        context.Conversation.Id,
                        context.DbTurn.Id,
                        context.TurnIndex,
                        currentMessageSequence++,
                        context.AssistantName,
                        context.ModelDeploymentId,
                        context.AssistantId,
                        ExpectedExecutionId: context.DbTurn.ExecutionId));

                if (isThinking)
                {
                    currentThinkingContent.Append(e.ContentDelta);
                }
                else
                {
                    currentAssistantContent.Append(e.ContentDelta);
                }

                if (progressCheckpoint.ShouldCheckpoint(e.ContentDelta?.Length ?? 0)
                    && segment != null)
                {
                    checkpointVersion++;
                    var checkpointedContent = currentAssistantContent.ToString();
                    var checkpointedThinking = currentThinkingContent.Length > 0
                        ? JsonSerializer.Serialize(
                            new[] { ChatThinkingBlock.ForThinking(currentThinkingContent.ToString(), string.Empty) },
                            JsonOptions)
                        : null;
                    var checkpointedContentLength = currentAssistantContent.Length;
                    var checkpointFlushCounter = progressCheckpoint.FlushCounter;
                    checkpointQueue.EnqueueCheckpoint(
                        segment,
                        context.DbTurn.Id,
                        checkpointedContent,
                        checkpointedThinking,
                        checkpointVersion,
                        context.DbTurn.ExecutionId,
                        onCheckpointSucceeded: !isThinking && !policy.SupportsExternalToolResume
                            ? () =>
                            {
                                if (!hubWork.Writer.TryWrite(() => policy.BroadcastStreamingProgressAsync(
                                        context.ConversationId,
                                        context.User,
                                        context.DbTurn.Id,
                                        checkpointedContentLength,
                                        checkpointFlushCounter,
                                        CancellationToken.None)))
                                {
                                    _logger.LogWarning(
                                        "Dropped streaming progress broadcast for {ConversationId}",
                                        context.ConversationId);
                                }
                            }
                            : null,
                        onCheckpointFailed: ex => checkpointPersistenceFailure ??= ex);
                }

                if (cancellationWasRequested || workerCt.IsCancellationRequested)
                {
                    return;
                }

                if (policy.SupportsExternalToolResume)
                {
                    var tokenPayload = new { role = "assistant", contentDelta = e.ContentDelta, turnId = context.DbTurn.Id };
                    TryWrite(new StreamingEvent(StreamingEventTypes.Token, JsonSerializer.Serialize(tokenPayload, JsonOptions)));
                }
                else
                {
                    var payload = new { role = "assistant", contentDelta = e.ContentDelta, timestamp = DateTime.UtcNow, turnId = context.DbTurn.Id };
                    TryWrite(new StreamingEvent(StreamingEventTypes.AssistantMessage, JsonSerializer.Serialize(payload, JsonOptions)));
                }
            };

            MessageAddedEventHandler onMessageAdded = (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Role))
                {
                    return;
                }

                if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    checkpointQueue.ThrowIfFailed();
                    if (!string.IsNullOrEmpty(e.ToolCallsJson))
                    {
                        pendingAssistantToolCallsJson = e.ToolCallsJson;
                        pendingAssistantToolCallsContent = policy.SanitizeAssistantContent(
                            e.Message ?? string.Empty,
                            filenameUrlMap,
                            fileUrlContext);
                    }

                    HandleAssistantMessageAdded(
                        e,
                        context,
                        policy,
                        fileUrlContext,
                        filenameUrlMap,
                        checkpointQueue,
                        () => currentMessageSequence++,
                        currentAssistantContent,
                        TryWrite,
                        ex => checkpointPersistenceFailure ??= ex);
                    currentThinkingContent.Clear();
                    return;
                }

                if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    checkpointQueue.ThrowIfFailed();
                    HandleToolMessageAdded(
                        e,
                        context,
                        policy,
                        fileUrlContext,
                        filenameUrlMap,
                        checkpointQueue,
                        () => currentMessageSequence++,
                        TryWrite,
                        ex =>
                        {
                            toolMessagePersistenceFailed = true;
                            checkpointPersistenceFailure ??= ex;
                        });
                }
            };

            try
            {
                workerCt.ThrowIfCancellationRequested();
                var httpClient = _httpClientFactory.CreateClient();

                if (policy.SupportsExternalToolResume)
                {
                    var invocationContext = new AntRunner.ToolCalling.InvocationContext(
                        ProjectId: context.Conversation.Notebook.ProjectId,
                        NotebookId: context.Conversation.NotebookId,
                        ConversationId: context.Conversation.Id,
                        OAuthUserAccessToken: context.ChatOptions.oAuthUserAccessToken)
                    {
                        TurnIndex = context.TurnIndex,
                        ExecutionId = context.DbTurn.ExecutionId,
                        AssistantId = context.AssistantId,
                        NotebookConversationMessageId = context.UserMessageId,
                        ToolActivitySink = activity =>
                        {
                            try
                            {
                                TryWrite(StreamingEvents.BuildToolActivityProgress(activity, context.DbTurn.Id));
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
                                var payload = new
                                {
                                    toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(evt.ToolCallsJson, JsonOptions),
                                    turnId = context.DbTurn.Id
                                };
                                // Do not publish this before the pending-client-tool
                                // terminalization and distributed unlock. A consumer may resume
                                // immediately after receiving this event.
                                deferredExternalToolCallEvent = new StreamingEvent(
                                    StreamingEventTypes.ExternalToolCall,
                                    JsonSerializer.Serialize(payload, JsonOptions));
                            }
                            catch
                            {
                                deferredExternalToolCallEvent = new StreamingEvent(
                                    StreamingEventTypes.ExternalToolCall,
                                    evt.ToolCallsJson);
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
                                TryWrite(StreamingEvents.BuildToolActivityProgress(activity, context.DbTurn.Id));
                            }
                            catch
                            {
                                // Activity metadata is best-effort; never interrupt tool execution.
                            }
                        },
                        cancellationToken: workerCt,
                        executionId: context.DbTurn.ExecutionId);
                }

                await FlushPersistenceQueueAsync(throwOnFailure: true).ConfigureAwait(false);
                if (toolMessagePersistenceFailed)
                {
                    throw new InvalidOperationException(
                        $"Tool result persistence failed for turn {context.DbTurn.Id}.");
                }

                var cancellationWasRequested = workerCt.IsCancellationRequested
                    || await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt);
                if (cancellationWasRequested)
                {
                    // A non-cooperative provider can return a normal result after Stop was
                    // requested. Cancellation wins over the provider's normal completion so
                    // Stop cannot be reported as a completed turn.
                    _logger.LogInformation(
                        "Provider returned after cancellation for conversation {ConversationId} turn {TurnId}; terminalizing as cancelled",
                        context.ConversationId,
                        context.DbTurn.Id);
                    cancellationEvent = await HandleCancellationAsync(
                        context,
                        currentAssistantMessageId,
                        currentAssistantContent,
                        currentThinkingContent,
                        assistantMessageIds,
                        TryWrite,
                        output,
                        currentMessageSequence,
                        toolMessagePersistenceFailed);
                    terminalizationConfirmed = true;
                    terminalEvent = cancellationEvent;
                    return;
                }

                if (output?.Status != null
                    && output.Status.Equals("pending_client_tool", StringComparison.OrdinalIgnoreCase))
                {
                    terminalizationAttempted = true;
                    terminalizationConfirmed = await TerminalizeTurnUntilConfirmedAsync(
                        ConversationTurnTerminalizer.BuildRequest(
                            context,
                            "pending_client_tool",
                            output,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds));
                    if (!terminalizationConfirmed)
                    {
                        if (!await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt))
                        {
                            throw new InvalidOperationException("Pending client tool turn terminalization was not confirmed.");
                        }
                    }

                    if (await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt))
                    {
                        cancellationEvent = await HandleCancellationAsync(
                            context,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds,
                            TryWrite,
                            output,
                            currentMessageSequence,
                            toolMessagePersistenceFailed);
                        terminalizationConfirmed = true;
                        terminalEvent = cancellationEvent;
                        return;
                    }

                    await RunBestEffortLifecycleOperationAsync(
                        () => PersistTraceSegmentAsync("partial", ct: noneCt),
                        "partial prompt trace",
                        context.ConversationId);
                    terminalEvent = new StreamingEvent(
                        StreamingEventTypes.PendingClientTool,
                        JsonSerializer.Serialize(new { turnId = context.DbTurn.Id }, JsonOptions));
                    return;
                }

                if (!thinkingEmittedInStream)
                {
                    StreamingEvents.EmitThinkingMessages(output, assistantMessageIds, writer, context.DbTurn.Id);
                }

                await RunBestEffortLifecycleOperationAsync(
                    () => RecordToolUsageAsync(context, noneCt),
                    "tool usage",
                    context.ConversationId);

                if (output != null)
                {
                    terminalizationAttempted = true;
                    terminalizationConfirmed = await TerminalizeTurnUntilConfirmedAsync(
                        ConversationTurnTerminalizer.BuildRequest(
                            context,
                            "completed",
                            output,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds));
                    if (!terminalizationConfirmed)
                    {
                        if (!await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt))
                        {
                            throw new InvalidOperationException("Completed turn terminalization was not confirmed.");
                        }
                    }

                    if (await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt))
                    {
                        _logger.LogInformation(
                            "A cancellation marker won while terminalizing conversation {ConversationId} turn {TurnId}; publishing cancelled",
                            context.ConversationId,
                            context.DbTurn.Id);
                        cancellationEvent = await HandleCancellationAsync(
                            context,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds,
                            TryWrite,
                            output,
                            currentMessageSequence,
                            toolMessagePersistenceFailed);
                        terminalizationConfirmed = true;
                        terminalEvent = cancellationEvent;
                        return;
                    }

                    if (output.Usage != null)
                    {
                        await RunBestEffortLifecycleOperationAsync(
                            () => RecordChatUsageAsync(
                                context,
                                output,
                                currentAssistantMessageId,
                                assistantMessageIds,
                                TryWrite,
                                noneCt),
                            "chat usage",
                            context.ConversationId);
                    }
                }
                else
                {
                    // A null provider result is still a terminal stream outcome. Do not let the
                    // registry report completion while the durable turn remains "streaming".
                    terminalizationAttempted = true;
                    terminalizationConfirmed = await TerminalizeTurnUntilConfirmedAsync(
                        ConversationTurnTerminalizer.BuildRequest(
                            context,
                            "completed",
                            output,
                            currentAssistantMessageId,
                            currentAssistantContent,
                            currentThinkingContent,
                            assistantMessageIds));
                    if (!terminalizationConfirmed)
                    {
                        if (!await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt))
                        {
                            throw new InvalidOperationException("Null provider result terminalization was not confirmed.");
                        }
                    }
                }

                await RegisterAndQueueNotebookSyncIfNeededAsync(context, output, workerCt);
                await RunBestEffortLifecycleOperationAsync(
                    () => PersistTraceSegmentAsync("completed", ct: noneCt),
                    "completed prompt trace",
                    context.ConversationId);
                streamingSucceeded = true;
            }
            catch (OperationCanceledException ex)
            {
                await FlushPersistenceQueueAsync(throwOnFailure: false).ConfigureAwait(false);
                try
                {
                    var partialOutput = (ex as ChatRunCancelledException)?.ChatRunOutput;
                    terminalizationAttempted = true;
                    cancellationEvent = await HandleCancellationAsync(
                        context,
                        currentAssistantMessageId,
                        currentAssistantContent,
                        currentThinkingContent,
                        assistantMessageIds,
                        TryWrite,
                        partialOutput,
                        currentMessageSequence,
                        toolMessagePersistenceFailed);
                    terminalizationConfirmed = true;
                    terminalEvent = cancellationEvent;
                }
                catch (Exception cancellationHandlingException)
                {
                    _logger.LogError(
                        cancellationHandlingException,
                        "Failed while handling cancellation for {ConversationId} turn {TurnIndex}",
                        context.Conversation.Id,
                        context.TurnIndex);

                    terminalEvent = new StreamingEvent(
                        StreamingEventTypes.Error,
                        JsonSerializer.Serialize(
                            StreamingErrorEnvelope.Build(cancellationHandlingException, context.DbTurn.Id),
                            JsonOptions));
                    return;
                }

                // Cancellation lifecycle data is already durable at the turn boundary. Do not
                // perform an additional trace write from the cancelled worker.
            }
            catch (ConversationTurnExecutionFencedException)
            {
                // Stop may fence this execution from another API instance before this worker
                // observes the cancellation token. The durable Stop transaction already owns
                // terminalization and partial-content preservation; do not turn the expected late
                // write rejection into a user-visible provider error or attempt to overwrite the
                // replacement execution.
                await FlushPersistenceQueueAsync(throwOnFailure: false).ConfigureAwait(false);
                executionWasFenced = true;
                terminalizationAttempted = true;
                terminalizationConfirmed = true;
                terminalEvent = new StreamingEvent(
                    StreamingEventTypes.Cancelled,
                    JsonSerializer.Serialize(new { turnId = context.DbTurn.Id }, JsonOptions));
            }
            catch (Exception ex)
            {
                // A replacement execution or the hard Stop fence may have won after the
                // provider/tool raised its exception. Do not let this old worker publish an
                // error or retry lifecycle writes against the replacement generation.
                await FlushPersistenceQueueAsync(throwOnFailure: false).ConfigureAwait(false);
                if (context.DbTurn.ExecutionId.HasValue
                    && !await _persistence.IsTurnExecutionActiveAsync(
                        context.DbTurn.Id,
                        context.DbTurn.ExecutionId.Value,
                        noneCt))
                {
                    terminalizationAttempted = true;
                    terminalizationConfirmed = true;
                    if (await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, noneCt))
                    {
                        terminalEvent = new StreamingEvent(
                            StreamingEventTypes.Cancelled,
                            JsonSerializer.Serialize(new { turnId = context.DbTurn.Id }, JsonOptions));
                    }

                    return;
                }

                Exception surfacedException = ex;
                ChatRunOutput? partialOutput = ex is ChatConversationException chatEx ? chatEx.ChatRunOutput : null;
                await RunBestEffortLifecycleOperationAsync(
                    () => PersistTraceSegmentAsync("failed", ex.Message, noneCt),
                    "failed prompt trace",
                    context.ConversationId);

                try
                {
                    var terminalStatus = ConversationTurnTerminalizer.MapTerminalStatus(partialOutput, ex);
                    terminalizationAttempted = true;
                    terminalizationConfirmed = await TerminalizeTurnUntilConfirmedAsync(
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
                            pruneIncompleteToolCalls:
                                !context.Policy.SupportsExternalToolResume && !toolMessagePersistenceFailed));
                    if (!terminalizationConfirmed)
                    {
                        throw new InvalidOperationException("Failed turn terminalization was not confirmed.");
                    }

                    await RunBestEffortLifecycleOperationAsync(
                        () => RecordToolUsageAsync(context, noneCt),
                        "tool usage",
                        context.ConversationId);

                    if (partialOutput?.Usage != null)
                    {
                        await RunBestEffortLifecycleOperationAsync(
                            () => RecordChatUsageAsync(
                                context,
                                partialOutput,
                                currentAssistantMessageId,
                                assistantMessageIds,
                                TryWrite,
                                noneCt),
                            "chat usage",
                            context.ConversationId);
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

                terminalEvent = new StreamingEvent(
                    StreamingEventTypes.Error,
                    JsonSerializer.Serialize(
                        StreamingErrorEnvelope.Build(surfacedException, context.DbTurn.Id),
                        JsonOptions));
            }
            finally
            {
                await FlushPersistenceQueueAsync(throwOnFailure: false).ConfigureAwait(false);
                var cancellationPreservationRequested = workerCt.IsCancellationRequested
                    || _streamRunRegistry.IsHardStopRequested(context.DbTurn.Id)
                    || executionWasFenced;

                if (cancellationPreservationRequested)
                {
                    try
                    {
                        if (pendingAssistantToolCallsJson != null)
                        {
                            await _persistence.TryPreserveStoppedAssistantToolCallsAsync(
                                context.ConversationId,
                                context.DbTurn.Id,
                                currentAssistantMessageId ?? checkpointQueue.LastMessageId,
                                currentAssistantContent.Length > 0
                                    ? currentAssistantContent.ToString()
                                    : pendingAssistantToolCallsContent,
                                pendingAssistantToolCallsJson,
                                context.AssistantId,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to preserve stopped assistant tool calls for {ConversationId} turn {TurnId}",
                            context.ConversationId,
                            context.DbTurn.Id);
                    }

                    try
                    {
                        var materialized = await _persistence.MaterializeMissingCancellationToolResultsAsync(
                            context.ConversationId,
                            context.DbTurn.Id,
                            CancellationToken.None).ConfigureAwait(false);
                        if (materialized > 0)
                        {
                            _logger.LogInformation(
                                "Materialized {MaterializedCount} cancellation tool result(s) after stop for {ConversationId} turn {TurnId}",
                                materialized,
                                context.ConversationId,
                                context.DbTurn.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to materialize cancellation tool results for {ConversationId} turn {TurnId}",
                            context.ConversationId,
                            context.DbTurn.Id);
                    }
                }

                if (!terminalizationConfirmed && !terminalizationAttempted)
                {
                    try
                    {
                        terminalizationAttempted = true;
                        terminalizationConfirmed = await TerminalizeTurnUntilConfirmedAsync(
                            ConversationTurnTerminalizer.BuildRequest(
                                context,
                                workerCt.IsCancellationRequested ? "cancelled" : "failed",
                                output: null,
                                currentAssistantMessageId,
                                currentAssistantContent,
                                currentThinkingContent,
                                assistantMessageIds,
                                terminationCode: workerCt.IsCancellationRequested
                                    ? "cancelled"
                                    : "stream_terminalization_failed",
                                terminationDetail: workerCt.IsCancellationRequested
                                    ? "Stream was cancelled by user"
                                    : "Conversation stream did not produce a terminal result.",
                                pruneIncompleteToolCalls:
                                    !cancellationPreservationRequested
                                    && !context.Policy.SupportsExternalToolResume
                                    && !toolMessagePersistenceFailed));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Unable to confirm terminal state for {ConversationId} turn {TurnId}",
                            context.ConversationId,
                            context.DbTurn.Id);
                    }
                }

                try
                {
                    hubWork.Writer.TryComplete();
                    try
                    {
                        await hubPump.WaitAsync(LifecycleBestEffortTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning(
                            "Timed out draining stream broadcasts for {ConversationId}; continuing lifecycle cleanup",
                            context.ConversationId);
                        _ = ObserveTaskAsync(hubPump);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Hub broadcast pump failed for {ConversationId}", context.ConversationId);
                    }

                    // The provider worker has exited at this point, even if durable
                    // terminalization could not be confirmed. Release attempts are bounded;
                    // recovery will fence and repair the turn if the database was unavailable.
                    await releaseStreamLockAsync();

                    // Unregister before publishing ExternalToolCall so a consumer that resumes
                    // immediately can register the same pending turn without colliding with the
                    // old worker's in-process registration.
                    if (!workerCompletionNotified)
                    {
                        onWorkerCompleted?.Invoke();
                        workerCompletionNotified = true;
                    }

                    if (deferredExternalToolCallEvent != null
                        && output?.Status?.Equals("pending_client_tool", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        try
                        {
                            // This event is intentionally published only after the pending turn
                            // and its lock are durable, so the receiver can resume immediately.
                            await policy.BroadcastEventAsync(
                                context.ConversationId,
                                deferredExternalToolCallEvent,
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Failed to broadcast deferred external tool call for {ConversationId}",
                                context.ConversationId);
                        }

                        if (!sseCt.IsCancellationRequested)
                        {
                            try
                            {
                                writer.TryWrite(deferredExternalToolCallEvent);
                            }
                            catch
                            {
                                // The SSE channel may have been closed by a disconnected client.
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to finalize stream lifecycle for {ConversationId}", context.ConversationId);
                }

                // The provider worker has exited, so the in-process registry must not strand the
                // turn while durable cleanup/recovery handles a dependency failure.
                if (!workerCompletionNotified)
                {
                    onWorkerCompleted?.Invoke();
                    workerCompletionNotified = true;
                }

                if (streamingSucceeded)
                {
                    try
                    {
                        await policy.OnCompleteAsync(
                            context.ConversationId,
                            context.DbTurn.Id,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to broadcast completed stream for {ConversationId}",
                            context.ConversationId);
                    }

                    if (!sseCt.IsCancellationRequested)
                    {
                        await ConversationStreamEventWriter.WriteTerminalAsync(
                            writer,
                            new StreamingEvent(
                                StreamingEventTypes.Complete,
                                JsonSerializer.Serialize(new { turnId = context.DbTurn.Id }, JsonOptions)),
                            TimeSpan.FromSeconds(2),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else if (terminalEvent != null)
                {
                    try
                    {
                        await policy.BroadcastEventAsync(
                            context.ConversationId,
                            terminalEvent,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to broadcast terminal stream event for {ConversationId}",
                            context.ConversationId);
                    }

                    if (!sseCt.IsCancellationRequested)
                    {
                        await ConversationStreamEventWriter.WriteTerminalAsync(
                            writer,
                            terminalEvent,
                            TimeSpan.FromSeconds(2),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }

                MaybeScheduleFirstTurnTitleGeneration(context);

                throttler?.Dispose();
                writer.TryComplete();
            }
        }, CancellationToken.None);
    }

    private void HandleAssistantMessageAdded(
        MessageAddedEventArgs e,
        ConversationStreamRunContext context,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        StreamingCheckpointPersistenceQueue checkpointQueue,
        Func<int> allocateMessageSequence,
        StringBuilder currentAssistantContent,
        Action<StreamingEvent> tryWrite,
        Action<Exception> onPersistenceFailure)
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

            checkpointQueue.EnqueueAssistantResponse(
                startRequestFactory: () => new StartAssistantMessageRequest(
                    context.Conversation.Id,
                    context.DbTurn.Id,
                    context.TurnIndex,
                    allocateMessageSequence(),
                    context.AssistantName,
                    context.ModelDeploymentId,
                    context.AssistantId,
                    Content: toolCallAssistantText,
                    IsStreaming: false,
                    ToolCallsJson: toolCallsJson,
                    ExpectedExecutionId: context.DbTurn.ExecutionId),
                finalizeRequestFactory: messageId => new AssistantMessageUpdateRequest(
                    messageId,
                    context.DbTurn.Id,
                    toolCallAssistantText,
                    Finalize: true,
                    ToolCallsJson: toolCallsJson,
                    ExpectedExecutionId: context.DbTurn.ExecutionId),
                onSucceeded: () =>
                {
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
                                turnId = context.DbTurn.Id,
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

                    if (policy.SupportsExternalToolResume)
                    {
                        EmitStreamMessage(
                            e.Role!,
                            e.Message ?? string.Empty,
                            policy,
                            fileUrlContext,
                            filenameUrlMap,
                            tryWrite,
                            context.DbTurn.Id);
                    }
                    else
                    {
                        tryWrite(BuildAssistantStreamEvent(
                            e.Message ?? string.Empty,
                            policy,
                            fileUrlContext,
                            filenameUrlMap,
                            context.DbTurn.Id));
                    }
                },
                onFailed: onPersistenceFailure);

            currentAssistantContent.Clear();

            return;
        }

        var sanitized = policy.SanitizeAssistantContent(e.Message ?? string.Empty, filenameUrlMap, fileUrlContext);
        checkpointQueue.EnqueueAssistantResponse(
            startRequestFactory: () => new StartAssistantMessageRequest(
                context.Conversation.Id,
                context.DbTurn.Id,
                context.TurnIndex,
                allocateMessageSequence(),
                context.AssistantName,
                context.ModelDeploymentId,
                context.AssistantId,
                Content: sanitized,
                IsStreaming: false,
                ExpectedExecutionId: context.DbTurn.ExecutionId),
            finalizeRequestFactory: messageId => new AssistantMessageUpdateRequest(
                messageId,
                context.DbTurn.Id,
                sanitized,
                Finalize: true,
                ToolCallsJson: string.IsNullOrEmpty(e.ToolCallsJson) ? null : e.ToolCallsJson,
                ExpectedExecutionId: context.DbTurn.ExecutionId),
            onSucceeded: () => tryWrite(BuildAssistantStreamEvent(
                sanitized,
                policy,
                fileUrlContext,
                filenameUrlMap,
                context.DbTurn.Id)),
            onFailed: onPersistenceFailure);
        currentAssistantContent.Clear();
    }

    private void HandleToolMessageAdded(
        MessageAddedEventArgs e,
        ConversationStreamRunContext context,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        StreamingCheckpointPersistenceQueue checkpointQueue,
        Func<int> allocateMessageSequence,
        Action<StreamingEvent> tryWrite,
        Action<Exception> onPersistenceFailure)
    {
        var sanitizedContent = policy.SanitizeToolContent(e.Message ?? string.Empty, fileUrlContext);
        var messageSequence = allocateMessageSequence();
        checkpointQueue.EnqueueToolMessage(
            new CreateToolMessageRequest(
                context.Conversation.Id,
                context.DbTurn.Id,
                context.TurnIndex,
                messageSequence,
                sanitizedContent,
                e.ToolCallId,
                e.FunctionName,
                context.AssistantId,
                context.AssistantName,
                ExpectedExecutionId: context.DbTurn.ExecutionId),
            _ =>
            {
                try
                {
                    policy.UpdateFilenameUrlMapFromToolMessage(
                        sanitizedContent,
                        fileUrlContext,
                        filenameUrlMap,
                        context.Conversation);
                }
                catch (Exception ex)
                {
                    // Filename mapping is derived metadata; persistence of the tool result above is the
                    // data-integrity boundary and must not be hidden by a best-effort mapping failure.
                    _logger.LogWarning(
                        ex,
                        "Failed to update tool filename map for conversation {ConversationId} turn {TurnIndex}",
                        context.Conversation.Id,
                        context.TurnIndex);
                }

                if (policy.SupportsExternalToolResume)
                {
                    EmitStreamMessage(
                        e.Role!,
                        sanitizedContent,
                        policy,
                        fileUrlContext,
                        filenameUrlMap,
                        tryWrite,
                        context.DbTurn.Id);
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
                        turnId = context.DbTurn.Id,
                        timestamp = DateTime.UtcNow
                    };
                    tryWrite(new StreamingEvent(
                        StreamingEventTypes.ToolResult,
                        JsonSerializer.Serialize(toolPayload, JsonOptions)));
                }
            },
            ex =>
            {
                if (ex is not ConversationTurnExecutionFencedException)
                {
                    _logger.LogError(
                        ex,
                        "Failed to persist tool result for conversation {ConversationId} turn {TurnIndex}",
                        context.Conversation.Id,
                        context.TurnIndex);
                }

                onPersistenceFailure(ex);
            });
    }

    private static void EmitStreamMessage(
        string role,
        string message,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        Action<StreamingEvent> tryWrite,
        Guid? turnId = null)
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

        var payload = new
        {
            role = role.ToLowerInvariant(),
            content = payloadContent,
            turnId,
            timestamp = DateTime.UtcNow
        };
        tryWrite(new StreamingEvent(eventType, JsonSerializer.Serialize(payload, JsonOptions)));
    }

    private static StreamingEvent BuildAssistantStreamEvent(
        string content,
        IConversationStreamPolicy policy,
        ConversationFileUrlContext fileUrlContext,
        IDictionary<string, string> filenameUrlMap,
        Guid turnId)
    {
        var eventType = policy.SupportsExternalToolResume
            ? StreamingEventTypes.Message
            : StreamingEventTypes.AssistantMessage;
        var payload = new { role = "assistant", content, turnId, timestamp = DateTime.UtcNow };
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
            totalTokens = output.Usage.TotalTokens,
            turnId = context.DbTurn.Id
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

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Best-effort lifecycle telemetry must not become an unobserved task exception.
        }
    }

    private async Task RunBestEffortLifecycleOperationAsync(
        Func<Task> operation,
        string operationName,
        Guid conversationId)
    {
        Task? operationTask = null;
        try
        {
            operationTask = operation();
            await operationTask.WaitAsync(LifecycleBestEffortTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Timed out while recording {OperationName} for {ConversationId}; continuing stream lifecycle cleanup",
                operationName,
                conversationId);
            if (operationTask != null)
            {
                _ = ObserveTaskAsync(operationTask);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed while recording {OperationName} for {ConversationId}",
                operationName,
                conversationId);
        }
    }

    private async Task RegisterAndQueueNotebookSyncIfNeededAsync(
        ConversationStreamRunContext context,
        ChatRunOutput? output,
        CancellationToken cancellationToken)
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
            await EnsureTurnExecutionCanPublishAsync(context, cancellationToken);
            var isPublished = context.Policy.UsageMode != ConversationUsageMode.Private;
            var runId = NotebookPathResolver.TryExtractRunIdFromWorkingDirectory(context.DbTurn.WorkingDirectory);
            var dbPaths = NotebookFileChangeReporter.GetDbRelativePaths(output, isPublished, runId);

            if (dbPaths.Count > 0)
            {
                await _notebookFileSyncService.RegisterFilesAsync(
                    context.Conversation.Notebook.Id,
                    dbPaths,
                    cancellationToken);
            }

            await EnsureTurnExecutionCanPublishAsync(context, cancellationToken);
            await _notebookFileSyncService.QueueReconcileAsync(context.Conversation.Notebook.Id, cancellationToken);
        }
        catch (ConversationTurnExecutionFencedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register/queue notebook sync after turn completion");
        }
    }

    private async Task EnsureTurnExecutionCanPublishAsync(
        ConversationStreamRunContext context,
        CancellationToken cancellationToken)
    {
        if (!context.DbTurn.ExecutionId.HasValue)
        {
            return;
        }

        if (!await _persistence.IsTurnExecutionActiveAsync(
                context.DbTurn.Id,
                context.DbTurn.ExecutionId.Value,
                cancellationToken))
        {
            throw new ConversationTurnExecutionFencedException(context.DbTurn.Id);
        }
    }

    private async Task<bool> TerminalizeTurnUntilConfirmedAsync(TerminalizeTurnRequest request)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Task<bool>? operationTask = null;
            try
            {
                operationTask = _persistence.TerminalizeTurnAsync(request, CancellationToken.None);
                if (await operationTask.WaitAsync(LifecycleBestEffortTimeout).ConfigureAwait(false))
                {
                    return true;
                }

                // A fenced worker can lose the race to recovery or a newer lifecycle owner.
                // That is not confirmation of this worker's terminal outcome: callers must not
                // publish complete/cancelled solely because an old execution was rejected.
                if (request.ExecutionId.HasValue)
                {
                    _logger.LogWarning(
                        "Turn {TurnId} was terminalized by another execution; this worker cannot confirm its requested outcome",
                        request.TurnId);
                    return false;
                }

                throw new InvalidOperationException(
                    $"Turn {request.TurnId} was not found while terminalizing conversation {request.ConversationId}.");
            }
            catch (TimeoutException)
            {
                if (operationTask != null)
                {
                    _ = ObserveTaskAsync(operationTask);
                }

                _logger.LogError(
                    "Timed out terminalizing turn {TurnId} for {ConversationId}; recovery will finish the lifecycle",
                    request.TurnId,
                    request.ConversationId);
                return false;
            }
            catch (Exception ex)
            {
                if (attempt == 1 || attempt == 4)
                {
                    _logger.LogError(
                        ex,
                        "Turn terminalization attempt {Attempt} failed for {ConversationId} turn {TurnId}; retrying",
                        attempt,
                        request.ConversationId,
                        request.TurnId);
                }

                if (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                }
            }
        }

        return false;
    }

    private async Task<StreamingEvent> HandleCancellationAsync(
        ConversationStreamRunContext context,
        Guid? currentAssistantMessageId,
        StringBuilder currentAssistantContent,
        StringBuilder currentThinkingContent,
        IReadOnlyList<Guid> assistantMessageIds,
        Action<StreamingEvent> tryWrite,
        ChatRunOutput? partialOutput,
        int nextMessageSequence,
        bool toolMessagePersistenceFailed)
    {
        // An explicit Stop already owns the durable fence or is racing it. Never start another
        // terminalization transaction from that worker; it can otherwise hold the same row locks
        // Stop needs. Ordinary local cancellation (including recovery/test cancellation) still
        // terminalizes here.
        var terminalizationConfirmed = _streamRunRegistry.IsHardStopRequested(context.DbTurn.Id);
        var executionStillActive = !terminalizationConfirmed
            && context.DbTurn.ExecutionId.HasValue
            && await _persistence.IsTurnExecutionActiveAsync(
                context.DbTurn.Id,
                context.DbTurn.ExecutionId.Value,
                CancellationToken.None);
        var cancellationAssistantMessageId = currentAssistantMessageId;
        var cancellationAssistantContent = new StringBuilder(currentAssistantContent.ToString());
        var cancellationThinkingContent = new StringBuilder(currentThinkingContent.ToString());
        string? cancellationToolCallsJson = null;

        var partialAssistant = partialOutput?.Messages?
            .LastOrDefault(message =>
                message.Role == AntRunner.Chat.Abstractions.ChatRole.Assistant);
        if (partialAssistant != null)
        {
            if (cancellationAssistantContent.Length == 0)
            {
                cancellationAssistantContent.Append(partialAssistant.GetText());
            }

            if (cancellationThinkingContent.Length == 0
                && partialAssistant.ThinkingBlocks is { Count: > 0 })
            {
                cancellationThinkingContent.Append(
                    string.Join(
                        Environment.NewLine,
                        partialAssistant.ThinkingBlocks
                            .Where(block => !string.IsNullOrWhiteSpace(block.Thinking))
                            .Select(block => block.Thinking)));
            }

            if (partialAssistant.ToolCalls is { Count: > 0 })
            {
                cancellationToolCallsJson = JsonSerializer.Serialize(partialAssistant.ToolCalls, JsonOptions);
            }
        }

        // A cancellation can race the first progress callback. The provider's partial output is
        // still authoritative even when the callback could not create its streaming row with the
        // cancelled worker token. Materialize that unacknowledged partial data only while this
        // execution still owns the turn; a hard-stop fence rejects the write.
        if (executionStillActive
            && !terminalizationConfirmed
            && (cancellationAssistantContent.Length > 0
                || cancellationThinkingContent.Length > 0
                || cancellationToolCallsJson != null))
        {
            try
            {
                var thinkingJson = cancellationThinkingContent.Length > 0
                    ? JsonSerializer.Serialize(
                        new[] { ChatThinkingBlock.ForThinking(cancellationThinkingContent.ToString(), string.Empty) },
                        JsonOptions)
                    : null;

                if (cancellationAssistantMessageId.HasValue)
                {
                    await _persistence.AppendOrFinalizeAssistantMessageAsync(
                        new AssistantMessageUpdateRequest(
                            cancellationAssistantMessageId.Value,
                            context.DbTurn.Id,
                            cancellationAssistantContent.ToString(),
                            Finalize: cancellationToolCallsJson != null,
                            ToolCallsJson: cancellationToolCallsJson,
                            ThinkingBlocksJson: thinkingJson,
                            ExpectedExecutionId: context.DbTurn.ExecutionId),
                        CancellationToken.None);
                }
                else
                {
                    cancellationAssistantMessageId = await _persistence.StartAssistantMessageAsync(
                        new StartAssistantMessageRequest(
                            context.Conversation.Id,
                            context.DbTurn.Id,
                            context.TurnIndex,
                            nextMessageSequence,
                            context.AssistantName,
                            context.ModelDeploymentId,
                            context.AssistantId,
                            Content: cancellationAssistantContent.ToString(),
                            IsStreaming: cancellationToolCallsJson == null,
                            ToolCallsJson: cancellationToolCallsJson,
                            ExpectedExecutionId: context.DbTurn.ExecutionId),
                        CancellationToken.None);
                    assistantMessageIds = assistantMessageIds
                        .Append(cancellationAssistantMessageId.Value)
                        .ToList();
                }

                if (partialAssistant?.ToolCalls is { Count: > 0 })
                {
                    foreach (var toolCall in partialAssistant.ToolCalls)
                    {
                        if (!toolCall.IsFunction || string.IsNullOrWhiteSpace(toolCall.Id))
                        {
                            continue;
                        }

                        await _persistence.CreateToolMessageAsync(
                            new CreateToolMessageRequest(
                                context.Conversation.Id,
                                context.DbTurn.Id,
                                context.TurnIndex,
                                nextMessageSequence++,
                                "ERROR: Operation was cancelled",
                                toolCall.Id,
                                toolCall.Function.Name,
                                context.AssistantId,
                                context.AssistantName,
                                ExpectedExecutionId: context.DbTurn.ExecutionId),
                            CancellationToken.None);
                    }
                }
            }
            catch (ConversationTurnExecutionFencedException)
            {
                // A hard-stop fence won between the active-generation read and this write. The
                // fence already owns durable partial-output preservation.
                terminalizationConfirmed = true;
            }
        }

        if (!terminalizationConfirmed
            && (executionStillActive || !context.DbTurn.ExecutionId.HasValue))
        {
            terminalizationConfirmed = await TerminalizeTurnUntilConfirmedAsync(
                ConversationTurnTerminalizer.BuildRequest(
                    context,
                    "cancelled",
                    partialOutput,
                    cancellationAssistantMessageId,
                    cancellationAssistantContent,
                    cancellationThinkingContent,
                    assistantMessageIds,
                    terminationCode: "cancelled",
                    terminationDetail: "Stream was cancelled by user",
                    pruneIncompleteToolCalls: false,
                    currentAssistantToolCallsJson: cancellationToolCallsJson));
        }
        if (!terminalizationConfirmed)
        {
            // A remote Stop may have already committed the durable fence before this worker
            // observed its cancellation signal. In that case the old execution is expected to
            // lose terminalization arbitration; the committed cancellation is the confirmation.
            if (!await _persistence.IsTurnCancellationRequestedAsync(context.DbTurn.Id, CancellationToken.None))
            {
                throw new InvalidOperationException("Cancelled turn terminalization was not confirmed.");
            }
        }

        await RunBestEffortLifecycleOperationAsync(
            () => RecordToolUsageAsync(context, CancellationToken.None),
            "tool usage",
            context.ConversationId);

        if (partialOutput?.Usage != null)
        {
            await RunBestEffortLifecycleOperationAsync(
                () => RecordChatUsageAsync(
                    context,
                    partialOutput,
                    currentAssistantMessageId,
                    assistantMessageIds,
                    tryWrite,
                    CancellationToken.None),
                "chat usage",
                context.ConversationId);
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
        return new StreamingEvent(
            StreamingEventTypes.Cancelled,
            JsonSerializer.Serialize(cancelPayload, JsonOptions));
    }

    private void MaybeScheduleFirstTurnTitleGeneration(ConversationStreamRunContext context)
    {
        if (context.TurnIndex != 1)
        {
            return;
        }

        if (!string.Equals(context.Conversation.Title, "New Conversation", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = GenerateFirstTurnTitleInBackgroundAsync(context.ConversationId);
    }

    private async Task GenerateFirstTurnTitleInBackgroundAsync(Guid conversationId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var result = await ConversationTitleGenerator.GenerateAndApplyAsync(db, conversationId);
            if (result.AttemptedGeneration
                && string.Equals(result.Title, "New Conversation", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Conversation title generator did not produce a title for conversation {ConversationId}",
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate title for conversation {ConversationId}", conversationId);
        }
    }
}
