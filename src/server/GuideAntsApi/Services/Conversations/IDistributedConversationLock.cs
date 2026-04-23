using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Service for managing distributed locks on conversations across multiple server instances.
/// Ensures only one user can stream to a conversation at a time.
/// </summary>
public interface IDistributedConversationLock
{
    /// <summary>
    /// Attempts to acquire a lock on a conversation.
    /// </summary>
    /// <returns>The lock if acquired, null if the conversation is already locked by another user</returns>
    Task<ConversationLock?> TryAcquireLockAsync(
        Guid conversationId, 
        string userName,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Releases a lock on a conversation.
    /// </summary>
    Task ReleaseLockAsync(Guid conversationId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the active lock for a conversation, if one exists and hasn't expired.
    /// </summary>
    Task<ConversationLock?> GetActiveLockAsync(
        Guid conversationId, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Cleans up all expired locks in the system.
    /// </summary>
    Task CleanupExpiredLocksAsync(CancellationToken cancellationToken = default);
}

