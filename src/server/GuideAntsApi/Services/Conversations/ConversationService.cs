using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Queries;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Services.Conversations;

public sealed class ConversationStopInProgressException(Guid turnId)
    : InvalidOperationException($"Stop is still in progress for turn {turnId}.")
{
    public Guid TurnId { get; } = turnId;
}

public class ConversationService : IConversationService
{
    private static readonly TimeSpan StopDatabaseTimeout = TimeSpan.FromMilliseconds(1800);

    /// <summary>
    /// Matches <c>ConversationStreamEngine</c>'s serializer options so every SSE payload this
    /// service emits - including the preflight <c>error</c> event - reaches the client in the
    /// same shape the engine's own events use.
    /// </summary>
    private static readonly JsonSerializerOptions StreamJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationPersistence _persistence;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly IConversationQueryService _queryService;
    private readonly IConversationCommandService _commandService;
    private readonly IConversationHistoryBuilder _historyBuilder;
    private readonly IAttachmentContentService _attachmentContentService;
    private readonly INotebookFileService? _notebookFileService;
    private readonly IConversationUndoService _undoService;
    private readonly PrivateConversationStreamPolicy _streamPolicy;
    private readonly IConversationStreamEngine _streamEngine;
    private readonly ConversationStreamRunRegistry _streamRunRegistry;
    private readonly IDistributedConversationLock _distributedLock;
    private readonly IConversationBroadcastHub _broadcastHub;
    private readonly IToolOAuthService? _toolOAuthService;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IServiceScopeFactory scopeFactory,
        IConversationPersistence persistence,
        IChatModelResolver chatModelResolver,
        IConversationQueryService queryService,
        IConversationCommandService commandService,
        IConversationHistoryBuilder historyBuilder,
        IAttachmentContentService attachmentContentService,
        INotebookFileService? notebookFileService,
        IConversationUndoService undoService,
        PrivateConversationStreamPolicy streamPolicy,
        IConversationStreamEngine streamEngine,
        ConversationStreamRunRegistry streamRunRegistry,
        IDistributedConversationLock distributedLock,
        IConversationBroadcastHub broadcastHub,
        ILogger<ConversationService> logger,
        IToolOAuthService? toolOAuthService = null)
    {
        _scopeFactory = scopeFactory;
        _persistence = persistence;
        _chatModelResolver = chatModelResolver;
        _queryService = queryService;
        _commandService = commandService;
        _historyBuilder = historyBuilder;
        _attachmentContentService = attachmentContentService;
        _notebookFileService = notebookFileService;
        _undoService = undoService;
        _streamPolicy = streamPolicy;
        _streamEngine = streamEngine;
        _streamRunRegistry = streamRunRegistry;
        _distributedLock = distributedLock;
        _broadcastHub = broadcastHub;
        _logger = logger;
        _toolOAuthService = toolOAuthService;
    }

    public Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId) =>
        _queryService.GetConversationByIdAsync(conversationId);

    public Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId) =>
        _queryService.GetConversationWithMessagesAsync(conversationId);

    public Task UndoLastForConversationAsync(Guid conversationId) =>
        _undoService.UndoLastForConversationAsync(conversationId);

    public Task UndoForConversationAsync(Guid conversationId, Guid messageId) =>
        _undoService.UndoForConversationAsync(conversationId, messageId);

    public Task EditMessageAsync(Guid messageId, string newContent) =>
        _commandService.EditMessageAsync(messageId, newContent);

    public Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId) =>
        _queryService.GetListAsync(notebookId);

    public Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title) =>
        _commandService.CreateConversationAsync(notebookId, title);

    public Task RenameConversationAsync(Guid conversationId, string title) =>
        _commandService.RenameConversationAsync(conversationId, title);

    public Task DeleteConversationAsync(Guid conversationId) =>
        _commandService.DeleteConversationAsync(conversationId);

    public Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query) =>
        _queryService.GetUserConversationsAsync(query);

    public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(
        Guid conversationId,
        SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Guid? resolvedAssistantId = null)
    {
        var user = await _streamPolicy.ResolveUserIdentityAsync(internalUserId: null, externalUserIdentity: null, CancellationToken.None);
        await foreach (var ev in SendMessageStreamCoreAsync(conversationId, request, user, cancellationToken, resolvedAssistantId))
        {
            yield return ev;
        }
    }

    public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsUserAsync(
        Guid conversationId,
        SendMessageRequest request,
        Guid actingUserId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var user = await ResolveStreamUserIdentityAsync(actingUserId, cancellationToken);
        await foreach (var ev in SendMessageStreamCoreAsync(conversationId, request, user, cancellationToken))
        {
            yield return ev;
        }
    }

    private async Task<StreamUserIdentity> ResolveStreamUserIdentityAsync(Guid userId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        var userName = string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name;
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new InvalidOperationException($"User {userId} has no display identity.");
        }

        return new StreamUserIdentity(user.Id, userName, null);
    }

    public async Task<bool> CancelTurnStreamAsync(Guid conversationId, Guid turnId)
    {
        // Signal a local worker, but do not wait for provider or tool termination. The durable
        // fence below is the authority that decides whether this Stop succeeded.
        var localCancellationSignalled = _streamRunRegistry.RequestHardStop(turnId, conversationId);
        _logger.LogInformation(
            "Stop cancellation signal sent for turn {TurnId} in conversation {ConversationId}: localWorker={LocalWorker}; registryActive={RegistryActive}",
            turnId,
            conversationId,
            localCancellationSignalled,
            _streamRunRegistry.IsActive(turnId));
        var fence = await RunStopDatabaseOperationAsync(
            turnId,
            ct => _persistence.FenceTurnCancellationAsync(
                conversationId,
                turnId,
                expectedExecutionId: null,
                ct: ct));
        if (!fence.Found)
        {
            return false;
        }

        if (fence.ConflictingLeasePresent)
        {
            // The target turn is not the owner of the visible lease. Do not detach a worker or
            // open a local gate when the authoritative fence refused this Stop.
            throw new ConversationStopInProgressException(turnId);
        }

        // Once the database fence commits, this worker is no longer a logical owner even if its
        // provider ignores cancellation. Remove it from admission tracking before opening the
        // local gate so a replacement Submit/Undo cannot be held behind the old worker.
        if (_streamRunRegistry.IsActive(turnId))
        {
            _streamRunRegistry.Detach(turnId);
        }

        if (!_streamRunRegistry.IsAnyActiveForConversation(conversationId))
        {
            _streamPolicy.TryReleaseFencedConversationGate(conversationId);
        }

        // FenceTurnCancellationAsync removes the old lease in the same serializable transaction
        // as the turn transition. This final check is only defensive: if an old lease is still
        // visible, Stop must fail closed rather than tell the client it is safe to proceed.
        _logger.LogInformation(
            "Stop confirmed for turn {TurnId} in conversation {ConversationId}; wasStreaming={WasStreaming}, oldWorkerDetached={OldWorkerDetached}, oldLeaseReleased={OldLeaseReleased}",
            turnId,
            conversationId,
            fence.WasStreaming,
            !_streamRunRegistry.IsActive(turnId),
            fence.PreviousLeaseWasReleased);
        return true;
    }

    private static async Task<T> RunStopDatabaseOperationAsync<T>(
        Guid turnId,
        Func<CancellationToken, Task<T>> operation)
    {
        var timeout = new CancellationTokenSource();
        Task<T>? operationTask = null;
        try
        {
            operationTask = operation(timeout.Token);
            var result = await operationTask.WaitAsync(StopDatabaseTimeout).ConfigureAwait(false);
            timeout.Dispose();
            return result;
        }
        catch (TimeoutException)
        {
            var cancellationTask = timeout.CancelAsync();
            if (operationTask != null)
            {
                _ = ObserveStopDatabaseOperationAsync(operationTask, timeout, cancellationTask);
            }
            else
            {
                timeout.Dispose();
            }
            throw new ConversationStopInProgressException(turnId);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timeout.Dispose();
            throw new ConversationStopInProgressException(turnId);
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    private static async Task ObserveStopDatabaseOperationAsync<T>(
        Task<T> operationTask,
        CancellationTokenSource timeout,
        Task cancellationTask)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch
        {
            // The bounded Stop request already returned its lifecycle result. Observe any
            // eventual provider completion so a late database failure is not unobserved.
        }
        finally
        {
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation callbacks are best-effort after the Stop request is bounded.
            }

            timeout.Dispose();
        }
    }

    private static async Task ObserveStopTaskAsync<T>(Task<T> operationTask)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch
        {
            // The lifecycle boundary was already confirmed by the worker. A late marker failure
            // must be observed without changing the completed Stop response.
        }
    }

    private async Task ReleaseStreamLockUntilConfirmedAsync(
        IStreamLockHandle lockHandle,
        Guid conversationId)
    {
        for (var releaseAttempt = 1; releaseAttempt <= 4; releaseAttempt++)
        {
            Task<bool>? releaseTask = null;
            try
            {
                releaseTask = lockHandle.ReleaseAsync(CancellationToken.None);
                if (await releaseTask.WaitAsync(StopDatabaseTimeout).ConfigureAwait(false))
                {
                    if (lockHandle.ConversationLockEventSent)
                    {
                        try
                        {
                            await _streamPolicy.OnUnlockAsync(conversationId, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to broadcast conversation unlock for {ConversationId}", conversationId);
                        }
                    }

                    return;
                }
            }
            catch (TimeoutException)
            {
                if (releaseTask != null)
                {
                    _ = ObserveReleaseTaskAsync(releaseTask);
                }

                _logger.LogWarning(
                    "Timed out releasing conversation lock for {ConversationId}; the lease will expire",
                    conversationId);
                return;
            }
            catch (Exception ex)
            {
                if (releaseAttempt == 1 || releaseAttempt == 4)
                {
                    _logger.LogWarning(
                        ex,
                        "Conversation lock release attempt {Attempt} failed for {ConversationId}",
                        releaseAttempt,
                        conversationId);
                }
            }

            if (releaseAttempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            }
        }

        _logger.LogWarning(
            "Conversation lock release is not confirmed for {ConversationId}; the lease will expire",
            conversationId);
    }

    private static async Task ObserveReleaseTaskAsync(Task<bool> releaseTask)
    {
        try
        {
            await releaseTask.ConfigureAwait(false);
        }
        catch
        {
            // The bounded cleanup attempt already returned; observe late release failures.
        }
    }

    private async Task TerminalizeTurnUntilConfirmedAsync(TerminalizeTurnRequest request)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Task<bool>? operationTask = null;
            try
            {
                operationTask = _persistence.TerminalizeTurnAsync(request, CancellationToken.None);
                if (await operationTask.WaitAsync(StopDatabaseTimeout).ConfigureAwait(false))
                {
                    return;
                }

                if (request.ExecutionId.HasValue)
                {
                    // A different execution fence has terminalized or reclaimed the turn.
                    return;
                }

                throw new InvalidOperationException(
                    $"Turn {request.TurnId} was not found while terminalizing conversation {request.ConversationId}.");
            }
            catch (TimeoutException)
            {
                if (operationTask != null)
                {
                    _ = ObserveReleaseTaskAsync(operationTask);
                }

                _logger.LogError(
                    "Timed out terminalizing turn {TurnId} for {ConversationId}; recovery will finish cleanup",
                    request.TurnId,
                    request.ConversationId);
                return;
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
    }

    public async IAsyncEnumerable<StreamingEvent> ObserveConversationEventsAsync(
        Guid conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var conversation = await _queryService.GetConversationByIdAsync(conversationId);
        if (conversation == null)
        {
            throw new KeyNotFoundException($"Conversation {conversationId} was not found.");
        }

        await foreach (var ev in _broadcastHub.SubscribeToConversationAsync(
            conversationId,
            Guid.NewGuid().ToString("N"),
            cancellationToken))
        {
            yield return ev;
        }
    }

    private async IAsyncEnumerable<StreamingEvent> SendMessageStreamCoreAsync(
        Guid conversationId,
        SendMessageRequest request,
        StreamUserIdentity user,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Guid? resolvedAssistantId = null)
    {
        var preflight = System.Diagnostics.Stopwatch.StartNew();
        var lockHandle = await _streamPolicy.TryAcquireStreamAsync(conversationId, user, CancellationToken.None);
        var lockMs = preflight.ElapsedMilliseconds;

        ConversationStreamRunContext? runContext = null;
        CancellationTokenSource? workerCts = null;
        var registeredTurnId = Guid.Empty;
        var setupCancelled = false;
        StreamSendContext? loaded = null;
        long loadMs = 0, turnMs = 0;

        // Lightweight create + register first so Stop has a turn id and a cancel token.
        // History/attachments after turn_created must honor that token (not CancellationToken.None).
        try
        {
            loaded = await LoadStreamMetadataAsync(
                conversationId,
                request,
                user,
                CancellationToken.None,
                resolvedAssistantId);
            loadMs = preflight.ElapsedMilliseconds;

            await CreateTurnAndUserMessageAsync(loaded, lockHandle.LeaseId, CancellationToken.None);
            turnMs = preflight.ElapsedMilliseconds;
            registeredTurnId = loaded.DbTurn!.Id;

            // Register the worker before setup completes so Stop can cancel setup as well as
            // inference. The worker is not considered stopped until its setup cleanup has
            // released the lock and unregistered the turn.
            workerCts = _streamRunRegistry.Register(registeredTurnId, conversationId);

            await _streamPolicy.OnTurnCreatedAsync(
                conversationId,
                new StreamTurnCreatedInfo(
                    loaded.DbTurn.Id,
                    loaded.TurnIndex,
                    request.Instructions,
                    loaded.AssistantName,
                    user),
                workerCts.Token);
        }
        catch
        {
            // Nothing has been yielded yet, so the SSE response has not started: the endpoint can
            // still turn this into a clean HTTP error. Release what we took and rethrow.
            try
            {
                if (loaded?.DbTurn != null && registeredTurnId != Guid.Empty)
                {
                    await TerminalizeTurnUntilConfirmedAsync(
                        new TerminalizeTurnRequest(
                            registeredTurnId,
                            conversationId,
                            loaded.TurnIndex,
                            "failed",
                            TerminationCode: "stream_setup_failed",
                            TerminationDetail: "Conversation stream setup failed.",
                            ExecutionId: loaded.DbTurn.ExecutionId));
                }

                await ReleaseStreamLockUntilConfirmedAsync(lockHandle, conversationId);
            }
            finally
            {
                if (workerCts != null && registeredTurnId != Guid.Empty)
                {
                    _streamRunRegistry.Unregister(registeredTurnId);
                }
            }
            throw;
        }

        var setupCt = workerCts!.Token;

        var resumedAfterTurnCreated = false;
        try
        {
            // The client arms Stop on this event; emit it before history/attachment setup so Stop
            // is available while the server is still assembling context.
            yield return new StreamingEvent(
                StreamingEventTypes.TurnCreated,
                JsonSerializer.Serialize(new { turnId = registeredTurnId }, StreamJsonOptions));
            resumedAfterTurnCreated = true;
        }
        finally
        {
            if (!resumedAfterTurnCreated)
            {
                _ = ContinueDetachedStreamAfterTurnCreatedAsync(
                    loaded!,
                    lockHandle,
                    workerCts.Token,
                    registeredTurnId);
            }
        }

        if (!resumedAfterTurnCreated)
        {
            yield break;
        }

        StreamingEvent? preflightFailure = null;
        long historyMs = 0, attachmentsMs = 0;
        try
        {
            await PopulateStreamHistoryAsync(loaded!, setupCt);
            historyMs = preflight.ElapsedMilliseconds;

            await ProcessAttachmentsAsync(loaded!, setupCt);
            attachmentsMs = preflight.ElapsedMilliseconds;

            runContext = BuildRunContext(_streamPolicy, loaded!, user);
        }
        catch (OperationCanceledException) when (workerCts.IsCancellationRequested)
        {
            setupCancelled = true;
            if (!_streamRunRegistry.IsHardStopRequested(registeredTurnId))
            {
                await TerminalizeTurnUntilConfirmedAsync(
                    new TerminalizeTurnRequest(
                        registeredTurnId,
                        conversationId,
                        loaded.TurnIndex,
                        "cancelled",
                        TerminationCode: "cancelled",
                        TerminationDetail: "Stream was cancelled by user",
                        ExecutionId: loaded.DbTurn?.ExecutionId));
            }

            try
            {
                await ReleaseStreamLockUntilConfirmedAsync(lockHandle, conversationId);
            }
            finally
            {
                _streamRunRegistry.Unregister(registeredTurnId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Preflight failed after turnCreated for conversation {ConversationId} turn {TurnId}",
                conversationId,
                registeredTurnId);

            // The turn row is already "streaming"; without this it would survive as a zombie.
            await TerminalizeTurnUntilConfirmedAsync(
                new TerminalizeTurnRequest(
                    registeredTurnId,
                    conversationId,
                    loaded.TurnIndex,
                    "failed",
                    TerminationCode: "preflight_failed",
                    TerminationDetail: ex.Message,
                    ExecutionId: loaded.DbTurn?.ExecutionId));

            var errorEvent = new StreamingEvent(
                StreamingEventTypes.Error,
                JsonSerializer.Serialize(StreamingErrorEnvelope.Build(ex), StreamJsonOptions));

            // Observers saw turn_created and must receive the terminal error before the unlock.
            try
            {
                await _streamPolicy.BroadcastEventAsync(conversationId, errorEvent, CancellationToken.None);
            }
            catch (Exception broadcastEx)
            {
                _logger.LogWarning(
                    broadcastEx,
                    "Failed to broadcast preflight error for {ConversationId}",
                    conversationId);
            }

            try
            {
                await ReleaseStreamLockUntilConfirmedAsync(lockHandle, conversationId);
            }
            finally
            {
                _streamRunRegistry.Unregister(registeredTurnId);
            }

            preflightFailure = errorEvent;
        }

        if (setupCancelled)
        {
            yield return new StreamingEvent(
                StreamingEventTypes.Cancelled,
                JsonSerializer.Serialize(new { turnId = registeredTurnId }, StreamJsonOptions));
            yield break;
        }

        if (preflightFailure != null)
        {
            yield return preflightFailure;
            yield break;
        }

        preflight.Stop();
        _logger.LogDebug(
            "Preflight timings for {ConversationId}: lock={LockMs}ms load={LoadMs}ms turn={TurnMs}ms history={HistoryMs}ms attachments={AttachmentsMs}ms total={TotalMs}ms",
            conversationId,
            lockMs,
            loadMs - lockMs,
            turnMs - loadMs,
            historyMs - turnMs,
            attachmentsMs - historyMs,
            preflight.ElapsedMilliseconds);

        Action onWorkerCompleted = () => _streamRunRegistry.Unregister(registeredTurnId);

        await foreach (var ev in _streamEngine.RunStreamAsync(
            runContext!,
            lockHandle,
            cancellationToken,
            workerCts!.Token,
            onWorkerCompleted))
        {
            yield return ev;
        }
    }

    private async Task ContinueDetachedStreamAfterTurnCreatedAsync(
        StreamSendContext loaded,
        IStreamLockHandle lockHandle,
        CancellationToken workerCt,
        Guid registeredTurnId)
    {
        var engineStarted = false;
        try
        {
            await PopulateStreamHistoryAsync(loaded, workerCt);
            await ProcessAttachmentsAsync(loaded, workerCt);
            var runContext = BuildRunContext(_streamPolicy, loaded, loaded.User);
            var onWorkerCompleted = () => _streamRunRegistry.Unregister(registeredTurnId);

            engineStarted = true;
            await foreach (var _ in _streamEngine.RunStreamAsync(
                runContext,
                lockHandle,
                CancellationToken.None,
                workerCt,
                onWorkerCompleted))
            {
            }
        }
        catch (OperationCanceledException) when (!engineStarted && workerCt.IsCancellationRequested)
        {
            if (!_streamRunRegistry.IsHardStopRequested(registeredTurnId))
            {
                await TerminalizeTurnUntilConfirmedAsync(
                    new TerminalizeTurnRequest(
                        registeredTurnId,
                        loaded.ConversationId,
                        loaded.TurnIndex,
                        "cancelled",
                        TerminationCode: "cancelled",
                        TerminationDetail: "Stream was cancelled by user",
                        ExecutionId: loaded.DbTurn?.ExecutionId));
            }

            try
            {
                await ReleaseStreamLockUntilConfirmedAsync(lockHandle, loaded.ConversationId);
            }
            finally
            {
                _streamRunRegistry.Unregister(registeredTurnId);
            }
        }
        catch (Exception ex) when (!engineStarted)
        {
            _logger.LogError(
                ex,
                "Detached conversation stream setup failed for {ConversationId} turn {TurnId}",
                loaded.ConversationId,
                registeredTurnId);

            try
            {
                await TerminalizeTurnUntilConfirmedAsync(
                    new TerminalizeTurnRequest(
                        registeredTurnId,
                        loaded.ConversationId,
                        loaded.TurnIndex,
                        "failed",
                        TerminationCode: "stream_setup_failed",
                        TerminationDetail: "Conversation stream setup failed.",
                        ExecutionId: loaded.DbTurn?.ExecutionId));
                await ReleaseStreamLockUntilConfirmedAsync(lockHandle, loaded.ConversationId);
            }
            finally
            {
                _streamRunRegistry.Unregister(registeredTurnId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Detached conversation stream failed for {ConversationId} turn {TurnId}",
                loaded.ConversationId,
                registeredTurnId);
        }
    }

    private sealed class StreamSendContext
    {
        public required Guid ConversationId { get; init; }
        public required NotebookConversation Conversation { get; init; }
        public required SendMessageRequest Request { get; init; }
        public required StreamUserIdentity User { get; init; }
        public required string AssistantName { get; init; }
        public string? ModelDeploymentId { get; init; }
        public ResolvedExecutionPolicy? ExecutionPolicy { get; init; }
        public required List<ChatMessage> PreviousMessages { get; set; }
        public Guid? AssistantId { get; init; }
        public required Dictionary<string, string> ExternalAuthTokens { get; set; }
        public int TurnIndex { get; set; }
        public ConversationTurn? DbTurn { get; set; }
        public NotebookConversationMessage? UserMessage { get; set; }
    }

    private async Task<StreamSendContext> LoadStreamMetadataAsync(
        Guid conversationId,
        SendMessageRequest request,
        StreamUserIdentity user,
        CancellationToken ct,
        Guid? resolvedAssistantId = null)
    {
        if (string.IsNullOrWhiteSpace(request.Instructions) && (request.Attachments == null || request.Attachments.Count == 0))
        {
            throw new ArgumentException("Instructions required", nameof(request));
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Guide)
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Project)
            .Include(c => c.Turns)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found");

        var activeTurn = conv.Turns
            .Where(t => string.Equals(t.Status, "streaming", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.TurnIndex)
            .FirstOrDefault();
        if (activeTurn != null)
        {
            throw new InvalidOperationException(
                $"Conversation already has streaming turn {activeTurn.Id}.");
        }

        var assistantName = string.IsNullOrWhiteSpace(request.AssistantName) ? "assistant" : request.AssistantName;
        var modelDeploymentId = request.ModelDeploymentId;
        if (string.IsNullOrWhiteSpace(modelDeploymentId))
        {
            var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName)
                ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}.");
            modelDeploymentId = assistantDef.Model;
        }

        var requestedModelDeploymentId = modelDeploymentId;
        var resolvedModel = _chatModelResolver.Resolve(modelDeploymentId);
        modelDeploymentId = resolvedModel.ModelId;
        _logger.LogInformation(
            "Conversation chat model resolved. ConversationId={ConversationId}, AssistantName={AssistantName}, RequestedModelId={RequestedModelId}, ResolvedModelId={ResolvedModelId}, ReferenceKind={ReferenceKind}, Authority={Authority}, ParameterKeys=[{ParameterKeys}]",
            LogValueSanitizer.Sanitize(conversationId),
            LogValueSanitizer.Sanitize(assistantName),
            LogValueSanitizer.Sanitize(string.IsNullOrWhiteSpace(requestedModelDeploymentId) ? "(unset)" : requestedModelDeploymentId),
            LogValueSanitizer.Sanitize(resolvedModel.ModelId),
            LogValueSanitizer.Sanitize(resolvedModel.ReferenceKind),
            LogValueSanitizer.Sanitize(resolvedModel.ExecutionPolicy.Authority),
            LogValueSanitizer.Sanitize(string.Join(", ", resolvedModel.ExecutionPolicy.Parameters.Keys)));

        var assistantId = resolvedAssistantId
            ?? await AssistantResolution.ResolveActiveAssistantIdAsync(db, assistantName, ct);

        return new StreamSendContext
        {
            ConversationId = conversationId,
            Conversation = conv,
            Request = request,
            User = user,
            AssistantName = assistantName,
            ModelDeploymentId = modelDeploymentId,
            ExecutionPolicy = resolvedModel.ExecutionPolicy,
            PreviousMessages = [],
            AssistantId = assistantId,
            ExternalAuthTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task PopulateStreamHistoryAsync(StreamSendContext ctx, CancellationToken ct)
    {
        ctx.PreviousMessages = await _historyBuilder.PrepareMessagesForAssistantAsync(
            ctx.Conversation,
            ctx.AssistantName,
            ctx.User.UserId!.Value,
            ct);

        ctx.ExternalAuthTokens = _toolOAuthService != null
            ? await _toolOAuthService.ResolveExternalAuthTokensForAssistantAsync(
                ctx.User.UserId!.Value,
                ctx.Conversation.Notebook.ProjectId,
                ctx.AssistantId,
                ctx.AssistantName,
                ct)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task CreateTurnAndUserMessageAsync(
        StreamSendContext ctx,
        Guid executionId,
        CancellationToken ct)
    {
        var turnResult = await _persistence.CreateNextTurnAsync(
            new CreateTurnRequest(
                ctx.ConversationId,
                ctx.AssistantName,
                ctx.ModelDeploymentId,
                ctx.Request.Instructions,
                InitialStatus: "streaming",
                ExecutionId: executionId),
            ct);

        var userResult = await _persistence.CreateUserMessageAsync(
            new CreateUserMessageRequest(
                ctx.Conversation.Id,
                turnResult.TurnIndex,
                MessageSequence: 1,
                Content: ctx.Request.Instructions,
                ModelDeploymentId: ctx.ModelDeploymentId,
                UserId: ctx.User.UserId,
                ExternalUserIdentity: null,
                AssistantId: ctx.AssistantId),
            ct);

        ctx.TurnIndex = turnResult.TurnIndex;
        ctx.DbTurn = turnResult.Turn;
        ctx.UserMessage = userResult.Message;
    }

    private async Task ProcessAttachmentsAsync(StreamSendContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Attachments == null || ctx.Request.Attachments.Count == 0)
        {
            return;
        }

        await _attachmentContentService.AddAttachmentsToUserMessageAsync(
            ctx.UserMessage!.Id,
            ctx.Conversation.NotebookId,
            ctx.Request.Attachments,
            ct);

        foreach (var attachment in ctx.Request.Attachments)
        {
            if (attachment.NotebookFileId.HasValue)
            {
                var attachmentContents = await _attachmentContentService.ExpandAttachmentToChatContentsAsync(
                    new MessageAttachment
                    {
                        NotebookFileId = attachment.NotebookFileId.Value,
                        UploadType = attachment.UploadType
                    },
                    ct);
                if (attachmentContents.Count > 0)
                {
                    ctx.PreviousMessages.Add(new ChatMessage(
                        AntRunner.Chat.Abstractions.ChatRole.User,
                        attachmentContents));
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(attachment.RelativePath))
            {
                continue;
            }

            if (_notebookFileService == null)
            {
                _logger.LogWarning(
                    "Skipping path attachment because notebook file service is unavailable. relativePath={RelativePath}",
                    LogValueSanitizer.Sanitize(attachment.RelativePath));
                continue;
            }

            var normalizedPath = attachment.RelativePath.Replace("\\", "/").Trim().TrimStart('/');

            if (attachment.UploadType == ContentUploadType.Folder)
            {
                var folderPath = BuildAttachmentPathForChat(normalizedPath);
                ctx.PreviousMessages.Add(new ChatMessage(AntRunner.Chat.Abstractions.ChatRole.User, $"Attachment (folder): {folderPath}"));
                continue;
            }

            var file = await _notebookFileService.GetFileAsync(
                ctx.Conversation.Notebook.ProjectId,
                ctx.Conversation.NotebookId,
                normalizedPath);
            if (file == null)
            {
                _logger.LogWarning(
                    "Path attachment file not found for conversation send. notebookId={NotebookId} relativePath={RelativePath}",
                    ctx.Conversation.NotebookId,
                    LogValueSanitizer.Sanitize(normalizedPath));
                continue;
            }

            await file.Value.Stream.DisposeAsync();
            var attachmentPath = BuildAttachmentPathForChat(normalizedPath);
            ctx.PreviousMessages.Add(new ChatMessage(AntRunner.Chat.Abstractions.ChatRole.User, $"Attachment: {attachmentPath}"));
        }
    }

    private static string BuildAttachmentPathForChat(string relativePath) =>
        ContextOptionFilesResolver.ToCwdRelativePath(relativePath, isPublished: false);

    private static ConversationStreamRunContext BuildRunContext(
        IConversationStreamPolicy policy,
        StreamSendContext ctx,
        StreamUserIdentity user) =>
        new()
        {
            Policy = policy,
            ConversationId = ctx.ConversationId,
            Conversation = ctx.Conversation,
            DbTurn = ctx.DbTurn!,
            TurnIndex = ctx.TurnIndex,
            AssistantName = ctx.AssistantName,
            AssistantId = ctx.AssistantId,
            ModelDeploymentId = ctx.ModelDeploymentId,
            ChatOptions = new ChatRunOptions
            {
                AssistantName = ctx.AssistantName,
                DeploymentId = ctx.ModelDeploymentId,
                Instructions = ctx.Request.Instructions,
                oAuthUserAccessToken = ctx.ExternalAuthTokens.FirstOrDefault().Value,
                ExternalAuthTokens = ctx.ExternalAuthTokens,
                ClientToolDefinitions = ctx.Request.ClientToolDefinitions,
                ExecutionPolicy = ctx.ExecutionPolicy
            },
            PreviousMessages = ctx.PreviousMessages,
            UserMessageId = ctx.UserMessage?.Id,
            User = user
        };

    private async Task<bool> ForceReleaseConversationLockAsync(
        Guid conversationId,
        Guid orphanedTurnId,
        Guid? expectedLeaseId)
    {
        if (_streamRunRegistry.IsAnyActiveForConversation(conversationId, orphanedTurnId))
        {
            _logger.LogWarning(
                "Refusing orphaned-turn lock release for {ConversationId}; another local stream is active",
                conversationId);
            return false;
        }

        // The caller already verified that no active distributed lock existed before
        // terminalization. Verify again immediately before touching the local gate; if a newer
        // stream acquired the conversation meanwhile, never release or over-release its lock.
        if (await RunStopDatabaseOperationAsync(
                orphanedTurnId,
                ct => _distributedLock.GetActiveLockAsync(conversationId, ct)) != null)
        {
            _logger.LogWarning(
                "Refusing orphaned-turn lock release for {ConversationId}; a distributed stream acquired the lock",
                conversationId);
            return false;
        }

        if (!_streamPolicy.TryReleaseOrphanedConversationGate(conversationId))
        {
            _logger.LogWarning(
                "Refusing orphaned-turn local gate release for {ConversationId}; another stream is acquiring the lock",
                conversationId);
            return false;
        }

        var activeLock = await RunStopDatabaseOperationAsync(
            orphanedTurnId,
            ct => _distributedLock.GetActiveLockAsync(conversationId, ct));
        if (activeLock != null)
        {
            _logger.LogError(
                "Distributed conversation lock appeared during orphan cleanup for {ConversationId}",
                conversationId);
            return false;
        }

        if (expectedLeaseId.HasValue)
        {
            var released = await RunStopDatabaseOperationAsync(
                orphanedTurnId,
                ct => _distributedLock.ReleaseLockAsync(
                    conversationId,
                    expectedLeaseId.Value,
                    ct));
            if (!released)
            {
                // A false result is safe only when the exact old lease is gone and no newer
                // active owner appeared. Never interpret it as permission to release a newer
                // lease or to report a clean lifecycle while that owner is present.
                var replacementLock = await RunStopDatabaseOperationAsync(
                    orphanedTurnId,
                    ct => _distributedLock.GetActiveLockAsync(conversationId, ct));
                if (replacementLock != null)
                {
                    _logger.LogWarning(
                        "Refusing orphaned-turn cleanup for {ConversationId}; a newer distributed lease is active",
                        conversationId);
                    return false;
                }
            }
        }

        // This path has no lease that can authoritatively announce an unlock. A newer stream may
        // acquire the distributed lock immediately after the final observation, so broadcasting
        // here would be a false unlock for that newer owner. The exact-lease release above is
        // conditional and cannot remove that newer owner's lock.
        return true;
    }
}
