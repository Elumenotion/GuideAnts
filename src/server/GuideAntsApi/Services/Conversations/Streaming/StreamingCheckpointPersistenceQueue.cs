using System.Threading.Channels;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Serializes assistant and tool persistence behind the provider callback boundary.
/// Assistant segments are explicit: a segment is started once, finalized once, and then the next
/// segment may be started. This prevents a long tool loop from reusing the first assistant row.
/// </summary>
internal sealed class StreamingCheckpointPersistenceQueue : IAsyncDisposable
{
    private readonly IConversationPersistence _persistence;
    private readonly Channel<WorkItem> _workChannel;
    private readonly Task _pump;
    private readonly object _stateGate = new();
    private readonly List<Guid> _assistantMessageIds = [];

    private AssistantSegment? _activeSegment;
    private AssistantSegment? _lastSegment;
    private Exception? _firstFailure;
    private bool _disposed;

    internal sealed class AssistantSegment
    {
        internal AssistantSegment(StartAssistantMessageRequest request)
        {
            Request = request;
            MessageIdTask = new TaskCompletionSource<Guid>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal StartAssistantMessageRequest Request { get; }
        internal TaskCompletionSource<Guid> MessageIdTask { get; }
        internal Guid? MessageId { get; set; }
    }

    private sealed class WorkItem
    {
        internal WorkItem(Func<Task> operation, Action<Exception>? onFailed = null)
        {
            Operation = operation;
            OnFailed = onFailed;
        }

        internal Func<Task> Operation { get; }
        internal Action<Exception>? OnFailed { get; }
    }

    public StreamingCheckpointPersistenceQueue(IConversationPersistence persistence)
    {
        _persistence = persistence;
        _workChannel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _pump = Task.Run(ProcessAsync);
    }

    internal bool HasActiveAssistantMessage
    {
        get
        {
            lock (_stateGate)
            {
                return _activeSegment != null;
            }
        }
    }

    internal Guid? ActiveMessageId
    {
        get
        {
            lock (_stateGate)
            {
                return _activeSegment?.MessageId;
            }
        }
    }

    internal Guid? LastMessageId
    {
        get
        {
            lock (_stateGate)
            {
                return _lastSegment?.MessageId;
            }
        }
    }

    internal IReadOnlyList<Guid> AssistantMessageIds
    {
        get
        {
            lock (_stateGate)
            {
                return [.. _assistantMessageIds];
            }
        }
    }

    internal AssistantSegment EnsureAssistantMessage(StartAssistantMessageRequest request)
    {
        lock (_stateGate)
        {
            if (_activeSegment != null)
            {
                return _activeSegment;
            }

            var segment = new AssistantSegment(request);
            _activeSegment = segment;
            _lastSegment = segment;
            Enqueue(
                new WorkItem(
                    () => ProcessStartAsync(segment),
                    ex =>
                    {
                        segment.MessageIdTask.TrySetException(ex);
                    }));
            return segment;
        }
    }

    internal void EnqueueCheckpoint(
        AssistantSegment segment,
        Guid turnId,
        string content,
        string? thinkingBlocksJson,
        int checkpointVersion,
        Guid? expectedExecutionId,
        Action? onCheckpointSucceeded,
        Action<Exception>? onCheckpointFailed)
    {
        Enqueue(
            new WorkItem(
                async () =>
                {
                    var messageId = await segment.MessageIdTask.Task.ConfigureAwait(false);
                    var checkpointed = await _persistence.CheckpointTurnAsync(
                        turnId,
                        messageId,
                        content,
                        thinkingBlocksJson,
                        checkpointVersion,
                        CancellationToken.None,
                        expectedExecutionId).ConfigureAwait(false);
                    if (!checkpointed)
                    {
                        throw new ConversationTurnExecutionFencedException(turnId);
                    }

                    onCheckpointSucceeded?.Invoke();
                },
                onCheckpointFailed));
    }

    internal void EnqueueAssistantResponse(
        Func<StartAssistantMessageRequest> startRequestFactory,
        Func<Guid, AssistantMessageUpdateRequest> finalizeRequestFactory,
        Action onSucceeded,
        Action<Exception> onFailed)
    {
        AssistantSegment? activeSegment;
        lock (_stateGate)
        {
            activeSegment = _activeSegment;
            if (activeSegment != null)
            {
                _activeSegment = null;
            }
        }

        if (activeSegment != null)
        {
            Enqueue(
                new WorkItem(
                    async () =>
                    {
                        var messageId = await activeSegment.MessageIdTask.Task.ConfigureAwait(false);
                        await _persistence.AppendOrFinalizeAssistantMessageAsync(
                            finalizeRequestFactory(messageId),
                            CancellationToken.None).ConfigureAwait(false);
                        onSucceeded();
                    },
                    onFailed));
            return;
        }

        var startRequest = startRequestFactory();
        var completedSegment = new AssistantSegment(startRequest);
        lock (_stateGate)
        {
            _lastSegment = completedSegment;
        }

        Enqueue(
            new WorkItem(
                async () =>
                {
                    await ProcessStartAsync(completedSegment).ConfigureAwait(false);
                    onSucceeded();
                },
                ex =>
                {
                    completedSegment.MessageIdTask.TrySetException(ex);
                    onFailed(ex);
                }));
    }

    internal void EnqueueToolMessage(
        CreateToolMessageRequest request,
        Action<CreateToolMessageResult> onSucceeded,
        Action<Exception> onFailed)
    {
        Enqueue(
            new WorkItem(
                async () =>
                {
                    var result = await _persistence.CreateToolMessageAsync(
                        request,
                        CancellationToken.None).ConfigureAwait(false);
                    onSucceeded(result);
                },
                onFailed));
    }

    internal void ThrowIfFailed()
    {
        Exception? failure;
        lock (_stateGate)
        {
            failure = _firstFailure;
        }

        if (failure != null)
        {
            throw failure;
        }
    }

    internal async Task FlushAsync(
        bool throwOnFailure = true,
        bool suppressExecutionFence = false)
    {
        var barrier = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(
                new WorkItem(
                    () =>
                    {
                        barrier.TrySetResult(true);
                        return Task.CompletedTask;
                    })))
        {
            throw new InvalidOperationException("Streaming persistence queue is closed.");
        }

        await barrier.Task.ConfigureAwait(false);

        Exception? failure;
        lock (_stateGate)
        {
            failure = _firstFailure;
        }

        if (!throwOnFailure || failure == null)
        {
            return;
        }

        if (suppressExecutionFence && failure is ConversationTurnExecutionFencedException)
        {
            return;
        }

        throw failure;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _workChannel.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        await foreach (var work in _workChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work.Operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    work.OnFailed?.Invoke(ex);
                }
                catch (Exception callbackException)
                {
                    RecordFailure(callbackException);
                }

                RecordFailure(ex);
            }
        }
    }

    private async Task ProcessStartAsync(AssistantSegment segment)
    {
        try
        {
            var messageId = await _persistence.StartAssistantMessageAsync(
                segment.Request,
                CancellationToken.None).ConfigureAwait(false);
            lock (_stateGate)
            {
                segment.MessageId = messageId;
                _assistantMessageIds.Add(messageId);
            }

            segment.MessageIdTask.TrySetResult(messageId);
        }
        catch (Exception ex)
        {
            segment.MessageIdTask.TrySetException(ex);
            throw;
        }
    }

    private void Enqueue(WorkItem work)
    {
        if (!TryEnqueue(work))
        {
            var failure = new InvalidOperationException("Streaming persistence queue is closed.");
            work.OnFailed?.Invoke(failure);
            RecordFailure(failure);
        }
    }

    private bool TryEnqueue(WorkItem work)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_workChannel.Writer.TryWrite(work))
            {
                return true;
            }
        }

        return false;
    }

    private void RecordFailure(Exception exception)
    {
        lock (_stateGate)
        {
            _firstFailure ??= exception;
        }
    }
}
