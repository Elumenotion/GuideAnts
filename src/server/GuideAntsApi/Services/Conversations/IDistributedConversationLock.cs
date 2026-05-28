using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations;

public enum LockAcquisitionStatus
{
    Acquired,
    AlreadyLocked,
    ConversationNotFound,
    RaceCondition
}

public sealed class LockAcquisitionResult
{
    public required LockAcquisitionStatus Status { get; init; }
    public ConversationLock? Lock { get; init; }
    public string? LockedByUserName { get; init; }

    public static LockAcquisitionResult Acquired(ConversationLock lockEntity) =>
        new() { Status = LockAcquisitionStatus.Acquired, Lock = lockEntity };

    public static LockAcquisitionResult AlreadyLocked(string lockedByUserName) =>
        new() { Status = LockAcquisitionStatus.AlreadyLocked, LockedByUserName = lockedByUserName };

    public static LockAcquisitionResult NotFound() =>
        new() { Status = LockAcquisitionStatus.ConversationNotFound };

    public static LockAcquisitionResult Race() =>
        new() { Status = LockAcquisitionStatus.RaceCondition };
}

/// <summary>
/// Service for managing distributed locks on conversations across multiple server instances.
/// Ensures only one user can stream to a conversation at a time.
/// </summary>
public interface IDistributedConversationLock
{
    Task<LockAcquisitionResult> TryAcquireLockAsync(
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

