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
        var streamGate = useLock ? await AcquireUndoLockAsync(conversationId) : null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            _logger.LogCritical("🚨 UNDO called for conversation {ConversationId}", conversationId);
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

                return;
            }

            var targetMessage = findTargetMessage(conv);
            if (targetMessage == null)
            {
                return;
            }

            var messagesToRemove = conv.Messages
                .Where(m => m.TurnIndex >= targetMessage.TurnIndex)
                .ToList();

            var turnsToRemove = await db.ConversationTurns
                .Where(t => t.NotebookConversationId == conversationId)
                .Where(t => t.TurnIndex >= targetMessage.TurnIndex)
                .ToListAsync();

            _logger.LogCritical(
                "🚨 UNDO removing {MessageCount} messages and {TurnCount} turns from turn {TurnIndex} onwards in conversation {ConversationId}",
                messagesToRemove.Count,
                turnsToRemove.Count,
                targetMessage.TurnIndex,
                conversationId);

            db.NotebookConversationMessages.RemoveRange(messagesToRemove);
            db.ConversationTurns.RemoveRange(turnsToRemove);
            await db.SaveChangesAsync();

            if (useLock)
            {
                await _broadcastHub.BroadcastToConversationAsync(conversationId,
                    new StreamingEvent(StreamingEventTypes.TurnRemoved, JsonSerializer.Serialize(new
                    {
                        turnIndex = targetMessage.TurnIndex,
                        messagesRemoved = messagesToRemove.Count,
                        timestamp = DateTime.UtcNow
                    }, JsonOptions)));
            }

            _logger.LogCritical("🚨 UNDO completed for conversation {ConversationId}", conversationId);
        }
        finally
        {
            if (streamGate != null)
            {
                await ReleaseUndoLockAsync(conversationId, streamGate);
            }
        }
    }

    private async Task<SemaphoreSlim> AcquireUndoLockAsync(Guid conversationId)
    {
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, "User", CancellationToken.None);

        if (lockResult.Status == LockAcquisitionStatus.ConversationNotFound)
        {
            throw new KeyNotFoundException("Conversation not found");
        }

        if (lockResult.Status != LockAcquisitionStatus.Acquired)
        {
            var streamingTurnId = await GetActiveStreamingTurnIdAsync(conversationId);
            if (streamingTurnId != null && _streamRunRegistry.IsActive(streamingTurnId.Value))
            {
                throw new InvalidOperationException($"Conversation is locked by {lockResult.LockedByUserName}");
            }

            if (_privateStreamPolicy.GetConversationGate(conversationId) is { CurrentCount: 0 })
            {
                throw new InvalidOperationException($"Conversation is locked by {lockResult.LockedByUserName}");
            }

            _logger.LogWarning(
                "Undo clearing orphaned conversation lock for {ConversationId} (previously held by {LockedBy})",
                conversationId,
                lockResult.LockedByUserName);
            await _distributedLock.ReleaseLockAsync(conversationId, CancellationToken.None);

            var retry = await _distributedLock.TryAcquireLockAsync(conversationId, "User", CancellationToken.None);
            if (retry.Status != LockAcquisitionStatus.Acquired)
            {
                throw new InvalidOperationException("Conversation is locked by another user");
            }
        }

        var streamGate = _privateStreamPolicy.GetOrCreateConversationGate(conversationId);
        if (!await streamGate.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            try
            {
                await _distributedLock.ReleaseLockAsync(conversationId, CancellationToken.None);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(releaseEx, "Failed to release conversation lock after undo gate timeout for {ConversationId}", conversationId);
            }

            throw new InvalidOperationException("Conversation is locked by another user");
        }

        return streamGate;
    }

    private async Task<Guid?> GetActiveStreamingTurnIdAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.Status == "streaming")
            .OrderByDescending(t => t.TurnIndex)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync();
    }

    private async Task ReleaseUndoLockAsync(Guid conversationId, SemaphoreSlim streamGate)
    {
        try
        {
            streamGate.Release();
        }
        catch (Exception gateEx)
        {
            _logger.LogWarning(gateEx, "Failed to release undo gate for {ConversationId}", conversationId);
        }

        try
        {
            await _distributedLock.ReleaseLockAsync(conversationId, CancellationToken.None);
            _logger.LogInformation("Released conversation lock during undo for {ConversationId}", conversationId);
        }
        catch (Exception releaseEx)
        {
            _logger.LogError(releaseEx, "Failed to release conversation lock during undo for {ConversationId}", conversationId);
        }
    }
}
