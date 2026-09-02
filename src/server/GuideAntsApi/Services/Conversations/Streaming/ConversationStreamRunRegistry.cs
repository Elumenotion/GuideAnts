using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Conversations.Streaming;

public enum StreamCancellationResult
{
    NotRegistered,
    Completed,
    StillRunning
}

/// <summary>
/// Tracks in-process conversation stream runs so explicit Stop can cancel the background worker
/// without making logical conversation ownership depend on the worker's physical exit.
/// </summary>
public sealed class ConversationStreamRunRegistry
{
    private readonly ConcurrentDictionary<Guid, ActiveRun> _activeRuns = new();
    private readonly ConcurrentDictionary<Guid, ActiveRun> _detachedRuns = new();
    private readonly ConcurrentDictionary<Guid, byte> _hardStopTurns = new();

    private sealed class ActiveRun
    {
        public required CancellationTokenSource Cts { get; init; }
        public Guid? ConversationId { get; init; }
        public object CancellationGate { get; } = new();
        public Task? CancellationTask { get; set; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Registers an in-process worker for <paramref name="turnId"/>.
    /// The returned CTS is not linked to the HTTP SSE client; only explicit Stop cancels it.
    /// </summary>
    public CancellationTokenSource Register(Guid turnId, Guid? conversationId = null)
    {
        var cts = new CancellationTokenSource();
        var run = new ActiveRun
        {
            Cts = cts,
            ConversationId = conversationId
        };

        if (!_activeRuns.TryAdd(turnId, run))
        {
            cts.Dispose();
            throw new InvalidOperationException($"A stream is already registered for turn {turnId}.");
        }

        return cts;
    }

    /// <summary>
    /// Marks a worker as fully stopped. Callers must invoke this only after all terminal
    /// persistence and lock-release work has completed.
    /// </summary>
    public void Unregister(Guid turnId)
    {
        if (_activeRuns.TryRemove(turnId, out var run)
            || _detachedRuns.TryRemove(turnId, out run))
        {
            _hardStopTurns.TryRemove(turnId, out _);
            run.Completion.TrySetResult();
            DisposeCancellationSource(run);
        }
    }

    public bool RequestCancel(Guid turnId, Guid? expectedConversationId = null)
    {
        if (!_activeRuns.TryGetValue(turnId, out var run))
        {
            return false;
        }

        if (expectedConversationId.HasValue && run.ConversationId != expectedConversationId)
        {
            return false;
        }

        return SignalCancellation(run);
    }

    /// <summary>
    /// Signals an explicit Stop whose durable fence is being committed by the caller. The worker
    /// must unwind without starting a competing terminalization transaction; the fence owns that
    /// lifecycle transition.
    /// </summary>
    public bool RequestHardStop(Guid turnId, Guid? expectedConversationId = null)
    {
        if (!_activeRuns.TryGetValue(turnId, out var run))
        {
            return false;
        }

        if (expectedConversationId.HasValue && run.ConversationId != expectedConversationId)
        {
            return false;
        }

        // Keep this marker independent of the active/detached dictionary transition. Stop may
        // detach the run while its worker is still unwinding, and the worker must observe the
        // hard-stop decision throughout that transition.
        _hardStopTurns[turnId] = 0;

        return SignalCancellation(run);
    }

    /// <summary>
    /// Returns whether the worker is unwinding because an explicit Stop owns the durable fence.
    /// Detached runs remain queryable until their physical worker exits.
    /// </summary>
    public bool IsHardStopRequested(Guid turnId)
    {
        return _hardStopTurns.ContainsKey(turnId);
    }

    /// <summary>
    /// Removes the worker from the local ownership registry after the durable turn fence has
    /// committed. The worker may still be physically unwinding; it no longer owns the logical
    /// conversation and must not prevent a replacement turn from starting.
    ///
    /// The cancellation source is intentionally not disposed here. The worker still holds its
    /// token and may be inside provider code. It becomes collectible once that worker exits.
    /// </summary>
    public bool Detach(Guid turnId)
    {
        if (!_activeRuns.TryRemove(turnId, out var run))
        {
            return false;
        }

        // Keep the source until the worker's completion callback arrives. Removing it from the
        // active map is the logical unlock; retaining it separately prevents a detached worker
        // from leaking its CancellationTokenSource forever.
        _detachedRuns[turnId] = run;
        return true;
    }

    /// <summary>
    /// Legacy bounded cancellation operation for callers that need to observe worker completion.
    /// The Stop endpoint must use <see cref="RequestCancel"/> and <see cref="Detach"/> instead;
    /// logical Stop is confirmed by the durable fence, not by provider termination.
    /// </summary>
    public async Task<StreamCancellationResult> RequestCancelAsync(
        Guid turnId,
        TimeSpan? waitTimeout = null)
    {
        if (!_activeRuns.TryGetValue(turnId, out var run))
        {
            return StreamCancellationResult.NotRegistered;
        }

        if (!SignalCancellation(run))
        {
            return StreamCancellationResult.NotRegistered;
        }

        try
        {
            await run.Completion.Task.WaitAsync(waitTimeout ?? TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return StreamCancellationResult.Completed;
        }
        catch (TimeoutException)
        {
            return StreamCancellationResult.StillRunning;
        }
    }

    private static bool SignalCancellation(ActiveRun run)
    {
        lock (run.CancellationGate)
        {
            if (run.CancellationTask != null)
            {
                return true;
            }

            try
            {
                // CancelAsync marks the token promptly but does not execute provider callbacks
                // inline on the Stop request thread. A provider callback must never turn Stop
                // into an unbounded synchronous wait.
                run.CancellationTask = run.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Unregister can win the race after the run was read from the dictionary. The
                // completion signal still represents the authoritative worker lifecycle.
                return true;
            }
        }

        return true;
    }

    private static void DisposeCancellationSource(ActiveRun run)
    {
        Task? cancellationTask;
        lock (run.CancellationGate)
        {
            cancellationTask = run.CancellationTask;
        }

        if (cancellationTask == null)
        {
            run.Cts.Dispose();
            return;
        }

        _ = DisposeAfterCancellationAsync(run.Cts, cancellationTask);
    }

    private static async Task DisposeAfterCancellationAsync(
        CancellationTokenSource cts,
        Task cancellationTask)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation callbacks are best-effort; worker completion remains authoritative.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// True while an in-process stream worker is registered for <paramref name="turnId"/>.
    /// Stale-turn recovery must not terminalize these; wall-clock silence during thinking is normal.
    /// </summary>
    public bool IsActive(Guid turnId) => _activeRuns.ContainsKey(turnId);

    /// <summary>
    /// True while a stream worker is still registered or detached-but-unwinding for
    /// <paramref name="turnId"/>. Undo and recovery must treat these as in-flight.
    /// </summary>
    public bool IsInFlight(Guid turnId) =>
        _activeRuns.ContainsKey(turnId) || _detachedRuns.ContainsKey(turnId);

    /// <summary>
    /// True while any registered worker for <paramref name="conversationId"/> is active.
    /// This prevents orphan recovery for an old turn from releasing a newer local worker's
    /// conversation gate.
    /// </summary>
    public bool IsAnyActiveForConversation(Guid conversationId, Guid? excludingTurnId = null) =>
        _activeRuns.Any(pair =>
            pair.Value.ConversationId == conversationId
            && (!excludingTurnId.HasValue || pair.Key != excludingTurnId.Value));
}
