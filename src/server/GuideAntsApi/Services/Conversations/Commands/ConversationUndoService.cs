using System.Data;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Streaming;
using Microsoft.EntityFrameworkCore;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations.Commands;

public interface IConversationUndoService
{
    Task UndoLastForConversationAsync(Guid conversationId);

    Task UndoForConversationAsync(Guid conversationId, Guid messageId);

    /// <summary>
    /// Removes the most recent turn for a published conversation. Published conversations have no
    /// distributed lock or observers, so this skips lock acquisition and unlock broadcasting.
    /// </summary>
    Task UndoLastWithoutLockAsync(Guid conversationId);
}

public sealed class ConversationUndoService : IConversationUndoService
{
    private static readonly TimeSpan LockCleanupTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDistributedConversationLock _distributedLock;
    private readonly IConversationBroadcastHub _broadcastHub;
    private readonly PrivateConversationStreamPolicy _privateStreamPolicy;
    private readonly ConversationStreamRunRegistry _streamRunRegistry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationUndoService> _logger;

    public ConversationUndoService(
        IDistributedConversationLock distributedLock,
        IConversationBroadcastHub broadcastHub,
        PrivateConversationStreamPolicy privateStreamPolicy,
        ConversationStreamRunRegistry streamRunRegistry,
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationUndoService> logger)
    {
        _distributedLock = distributedLock;
        _broadcastHub = broadcastHub;
        _privateStreamPolicy = privateStreamPolicy;
        _streamRunRegistry = streamRunRegistry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task UndoLastForConversationAsync(Guid conversationId) =>
        UndoFromTurnAsync(conversationId, LastUserMessage, useLock: true);

    public Task UndoForConversationAsync(Guid conversationId, Guid messageId) =>
        UndoFromTurnAsync(conversationId, findTargetMessage: conv =>
            conv.Messages.FirstOrDefault(m => m.Id == messageId)
            ?? throw new KeyNotFoundException("Message not found"),
            useLock: true);

    public Task UndoLastWithoutLockAsync(Guid conversationId) =>
        UndoFromTurnAsync(conversationId, LastUserMessage, useLock: false);

    private static NotebookConversationMessage? LastUserMessage(NotebookConversation conv) =>
        conv.Messages
            .Where(m => m.Role == DataModelChatRole.User)
            .OrderByDescending(m => m.TurnIndex)
            .ThenByDescending(m => m.MessageSequence)
            .FirstOrDefault();

    private async Task UndoFromTurnAsync(
        Guid conversationId,
        Func<NotebookConversation, NotebookConversationMessage?> findTargetMessage,
        bool useLock)
    {
        var streamLease = useLock ? await AcquireUndoLockAsync(conversationId) : null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            Func<Task<(int TurnIndex, int MessagesRemoved)?>> mutationOperation = async () =>
            {
                _logger.LogInformation("Undo called for conversation {ConversationId}", conversationId);
                var conv = await db.NotebookConversations
                    .Include(c => c.Notebook)
                    .Include(c => c.Messages)
                        .ThenInclude(m => m.EditHistory)
                    .FirstOrDefaultAsync(c => c.Id == conversationId);

                if (conv == null)
                {
                    if (useLock)
                    {
                        throw new KeyNotFoundException("Conversation not found");
                    }

                    return null;
                }

                var targetMessage = findTargetMessage(conv);
                if (targetMessage == null)
                {
                    return null;
                }

                var messagesToRemove = conv.Messages
                    .Where(m => m.TurnIndex >= targetMessage.TurnIndex)
                    .ToList();

                var turnsToRemove = await db.ConversationTurns
                    .Where(t => t.NotebookConversationId == conversationId)
                    .Where(t => t.TurnIndex >= targetMessage.TurnIndex)
                    .ToListAsync();

                // Refuse undo while any in-process run for these turns is still alive so we never
                // delete rows a worker will later try to terminalize or trace.
                var activeTurn = turnsToRemove.FirstOrDefault(t => _streamRunRegistry.IsInFlight(t.Id));
                if (activeTurn != null)
                {
                    throw new InvalidOperationException(
                        $"Cannot undo while turn {activeTurn.TurnIndex} is still streaming");
                }

                // A worker on another API instance is invisible to this process. A durable
                // streaming row is therefore never treated as an orphan merely because the local
                // registry is empty; Stop/recovery must terminalize it before Undo can delete its
                // rows.
                var durableStreamingTurn = turnsToRemove
                    .FirstOrDefault(t => string.Equals(t.Status, "streaming", StringComparison.OrdinalIgnoreCase));
                if (durableStreamingTurn != null)
                {
                    throw new InvalidOperationException(
                        $"Cannot undo while turn {durableStreamingTurn.TurnIndex} is still streaming");
                }

                _logger.LogInformation(
                    "Undo removing {MessageCount} messages and {TurnCount} turns from turn {TurnIndex} onwards in conversation {ConversationId}",
                    messagesToRemove.Count,
                    turnsToRemove.Count,
                    targetMessage.TurnIndex,
                    conversationId);

                db.NotebookConversationMessages.RemoveRange(messagesToRemove);
                db.ConversationTurns.RemoveRange(turnsToRemove);
                await db.SaveChangesAsync();
                return (targetMessage.TurnIndex, messagesToRemove.Count);
            };

            // Private Undo already holds the distributed conversation lock, so its single
            // SaveChanges call supplies the atomic delete. Do not add an outer EF transaction:
            // with MARS enabled that only disables savepoints and emits the warning on every
            // normal Undo request. Published Undo has no lock and retains the serializable
            // boundary below.
            var mutation = useLock
                ? await ExecuteImplicitWriteAsync(db, mutationOperation)
                : await ExecuteSerializableWriteAsync(db, mutationOperation);

            if (!mutation.HasValue)
            {
                return;
            }

            if (useLock)
            {
                await _broadcastHub.BroadcastToConversationAsync(conversationId,
                    new StreamingEvent(StreamingEventTypes.TurnRemoved, JsonSerializer.Serialize(new
                    {
                        turnIndex = mutation.Value.TurnIndex,
                        messagesRemoved = mutation.Value.MessagesRemoved,
                        timestamp = DateTime.UtcNow
                    }, JsonOptions)));
            }

            _logger.LogInformation("Undo completed for conversation {ConversationId}", conversationId);
        }
        finally
        {
            if (streamLease != null)
            {
                await ReleaseUndoLockAsync(conversationId, streamLease);
            }
        }
    }

    private async Task<UndoLockLease> AcquireUndoLockAsync(Guid conversationId)
    {
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, "User", CancellationToken.None);

        if (lockResult.Status == LockAcquisitionStatus.ConversationNotFound)
        {
            throw new KeyNotFoundException("Conversation not found");
        }

        if (lockResult.Status != LockAcquisitionStatus.Acquired)
        {
            // A lock that is not visible in this process may belong to a worker on another API
            // instance. Never infer orphaned ownership from the local registry or semaphore.
            throw new InvalidOperationException(
                $"Conversation is locked by {lockResult.LockedByUserName ?? "another user"}");
        }

        var acquiredLock = lockResult.Lock
            ?? throw new InvalidOperationException("Distributed lock acquisition returned no lease.");
        var streamGate = _privateStreamPolicy.GetOrCreateConversationGate(conversationId);
        var gateAcquired = false;
        try
        {
            if (!await streamGate.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
            {
                throw new InvalidOperationException("Conversation is locked by another user");
            }

            gateAcquired = true;
            return new UndoLockLease(streamGate, acquiredLock.LeaseId);
        }
        catch
        {
            if (!gateAcquired)
            {
                await ReleaseDistributedLockUntilConfirmedAsync(
                    conversationId,
                    acquiredLock.LeaseId);
            }

            throw;
        }
    }

    private static async Task<T> ExecuteImplicitWriteAsync<T>(
        ApplicationDbContext db,
        Func<Task<T>> operation)
    {
        if (string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            return await operation();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            try
            {
                return await operation();
            }
            catch
            {
                // SaveChanges owns the implicit transaction. A retry still needs a fresh
                // tracker because a failed delete batch leaves entries in Deleted state.
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static async Task<T> ExecuteSerializableWriteAsync<T>(
        ApplicationDbContext db,
        Func<Task<T>> operation)
    {
        if (string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            return await operation();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var result = await operation();
                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                // MARS disables EF's automatic savepoints. Roll the transaction back before
                // execution-strategy retry and detach every tracked delete from the failed
                // attempt so a retry starts from a clean unit of work.
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }

                throw;
            }
        });
    }

    private async Task ReleaseUndoLockAsync(Guid conversationId, UndoLockLease streamLease)
    {
        await ReleaseDistributedLockUntilConfirmedAsync(conversationId, streamLease.LeaseId);

        try
        {
            streamLease.StreamGate.Release();
        }
        catch (SemaphoreFullException)
        {
            // The gate is already available; cleanup is idempotent.
        }
        catch (Exception gateEx)
        {
            _logger.LogWarning(gateEx, "Failed to release undo gate for {ConversationId}", conversationId);
        }

        _logger.LogInformation("Released conversation lock during undo for {ConversationId}", conversationId);
    }

    private async Task ReleaseDistributedLockUntilConfirmedAsync(Guid conversationId, Guid leaseId)
    {
        for (var releaseAttempt = 1; releaseAttempt <= 4; releaseAttempt++)
        {
            Task<bool>? releaseTask = null;
            Task<ConversationLock?>? activeLockTask = null;
            try
            {
                releaseTask = _distributedLock.ReleaseLockAsync(
                    conversationId,
                    leaseId,
                    CancellationToken.None);
                var released = await releaseTask.WaitAsync(LockCleanupTimeout).ConfigureAwait(false);
                if (released)
                {
                    return;
                }

                activeLockTask = _distributedLock.GetActiveLockAsync(
                    conversationId,
                    CancellationToken.None);
                var activeLock = await activeLockTask.WaitAsync(LockCleanupTimeout).ConfigureAwait(false);
                if (activeLock?.LeaseId != leaseId)
                {
                    // The lease is already gone or has been replaced. It is no longer safe or
                    // necessary for this undo request to release anything else.
                    return;
                }
            }
            catch (TimeoutException)
            {
                if (releaseTask != null)
                {
                    _ = ObserveTaskAsync(releaseTask);
                }

                if (activeLockTask != null)
                {
                    _ = ObserveTaskAsync(activeLockTask);
                }

                _logger.LogWarning(
                    "Timed out releasing conversation lock during undo for {ConversationId}; the lease will expire",
                    conversationId);
                return;
            }
            catch (Exception releaseEx)
            {
                if (releaseAttempt == 1 || releaseAttempt == 4)
                {
                    _logger.LogError(
                        releaseEx,
                        "Failed to release conversation lock during undo for {ConversationId}; retrying",
                        conversationId);
                }
            }

            if (releaseAttempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            }
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The bounded cleanup attempt already returned; observe late release failures.
        }
    }

    private sealed record UndoLockLease(SemaphoreSlim StreamGate, Guid LeaseId);
}
