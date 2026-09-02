using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Database-backed distributed locking service for conversation collaboration.
/// Provides cross-container coordination for single-writer scenarios.
/// </summary>
public class DistributedConversationLockService : IDistributedConversationLock
{
    private static readonly TimeSpan DefaultLockDuration = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DistributedConversationLockService> _logger;

    public DistributedConversationLockService(
        IServiceScopeFactory scopeFactory,
        ILogger<DistributedConversationLockService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<LockAcquisitionResult> TryAcquireLockAsync(
        Guid conversationId, 
        string userName,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var now = DateTime.UtcNow;
            var existingLock = await db.ConversationLocks
                .Where(l => l.ConversationId == conversationId)
                .FirstOrDefaultAsync(cancellationToken);
            
            if (existingLock != null)
            {
                // Check if expired
                if (existingLock.ExpiresAt <= now)
                {
                    // Delete only the lease observed by this attempt. A second contender may
                    // have removed it and acquired a new lease between the read and this cleanup.
                    // Never issue an un-fenced delete by conversation id.
                    int removed;
                    if (string.Equals(
                            db.Database.ProviderName,
                            "Microsoft.EntityFrameworkCore.InMemory",
                            StringComparison.Ordinal))
                    {
                        var lockToRemove = await db.ConversationLocks
                            .Where(l =>
                                l.ConversationId == conversationId
                                && l.LeaseId == existingLock.LeaseId
                                && l.ExpiresAt <= now)
                            .FirstOrDefaultAsync(cancellationToken);
                        if (lockToRemove == null)
                        {
                            return LockAcquisitionResult.Race();
                        }

                        db.ConversationLocks.Remove(lockToRemove);
                        removed = await db.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        removed = await db.ConversationLocks
                            .Where(l =>
                                l.ConversationId == conversationId
                                && l.LeaseId == existingLock.LeaseId
                                && l.ExpiresAt <= now)
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    if (removed == 0)
                    {
                        return LockAcquisitionResult.Race();
                    }

                    _logger.LogInformation("Removed expired lock for conversation {ConversationId}", conversationId);
                }
                else
                {
                    _logger.LogInformation("Conversation {ConversationId} is locked by {LockedByUserName}", 
                        conversationId, existingLock.LockedByUserName);
                    return LockAcquisitionResult.AlreadyLocked(existingLock.LockedByUserName);
                }
            }
            
            // Acquire lock
            var newLock = new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = Guid.NewGuid(),
                LockedByUserName = userName,
                LockedAt = now,
                ExpiresAt = now.Add(DefaultLockDuration)
            };
            
            db.ConversationLocks.Add(newLock);
            await db.SaveChangesAsync(cancellationToken);
            
            _logger.LogInformation("Acquired lock for conversation {ConversationId} by user {UserName}", 
                conversationId, userName);
            
            return LockAcquisitionResult.Acquired(newLock);
        }
        catch (DbUpdateException ex)
        {
            if (ex.InnerException is SqlException sqlEx &&
                sqlEx.Number == 547 &&
                sqlEx.Message.Contains("FK_ConversationLocks_NotebookConversations_ConversationId", StringComparison.Ordinal))
            {
                _logger.LogWarning("Conversation {ConversationId} does not exist — cannot acquire lock", conversationId);
                return LockAcquisitionResult.NotFound();
            }

            _logger.LogWarning(ex, "Race condition acquiring lock for conversation {ConversationId}", conversationId);
            return LockAcquisitionResult.Race();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring lock for conversation {ConversationId}", conversationId);
            throw;
        }
    }
    
    public async Task<bool> ReleaseLockAsync(
        Guid conversationId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            int removed;
            if (string.Equals(
                    db.Database.ProviderName,
                    "Microsoft.EntityFrameworkCore.InMemory",
                    StringComparison.Ordinal))
            {
                var lockToRemove = await db.ConversationLocks
                    .Where(l => l.ConversationId == conversationId && l.LeaseId == leaseId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (lockToRemove == null)
                {
                    return false;
                }

                db.ConversationLocks.Remove(lockToRemove);
                removed = await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                removed = await db.ConversationLocks
                    .Where(l => l.ConversationId == conversationId && l.LeaseId == leaseId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (removed > 0)
            {
                _logger.LogInformation("Released lock for conversation {ConversationId}", conversationId);
            }

            return removed > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing lock for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    public async Task<bool> RenewLockAsync(
        Guid conversationId,
        Guid leaseId,
        string userName,
        TimeSpan lockTtl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return false;
        }

        var ttl = lockTtl > TimeSpan.Zero ? lockTtl : DefaultLockDuration;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var now = DateTime.UtcNow;
            if (string.Equals(
                    db.Database.ProviderName,
                    "Microsoft.EntityFrameworkCore.InMemory",
                    StringComparison.Ordinal))
            {
                var lockToRenew = await db.ConversationLocks
                    .Where(l =>
                        l.ConversationId == conversationId
                        && l.LeaseId == leaseId
                        && l.LockedByUserName == userName
                        && l.ExpiresAt > now)
                    .FirstOrDefaultAsync(cancellationToken);
                if (lockToRenew == null)
                {
                    return false;
                }

                lockToRenew.ExpiresAt = now.Add(ttl);
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var updated = await db.ConversationLocks
                .Where(l =>
                    l.ConversationId == conversationId
                    && l.LeaseId == leaseId
                    && l.LockedByUserName == userName
                    && l.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(l => l.ExpiresAt, now.Add(ttl)),
                    cancellationToken);
            return updated > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing lock for conversation {ConversationId}", conversationId);
            return false;
        }
    }
    
    public async Task<ConversationLock?> GetActiveLockAsync(
        Guid conversationId, 
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var activeLock = await db.ConversationLocks
                .Where(l => l.ConversationId == conversationId)
                .Where(l => l.ExpiresAt > DateTime.UtcNow)
                .FirstOrDefaultAsync(cancellationToken);
            
            return activeLock;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active lock for conversation {ConversationId}", conversationId);
            throw;
        }
    }
    
    public async Task CleanupExpiredLocksAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var now = DateTime.UtcNow;
            int removed;
            if (string.Equals(
                    db.Database.ProviderName,
                    "Microsoft.EntityFrameworkCore.InMemory",
                    StringComparison.Ordinal))
            {
                var expiredLocks = await db.ConversationLocks
                    .Where(l => l.ExpiresAt <= now)
                    .ToListAsync(cancellationToken);
                if (expiredLocks.Count == 0)
                {
                    return;
                }

                db.ConversationLocks.RemoveRange(expiredLocks);
                removed = await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                removed = await db.ConversationLocks
                    .Where(l => l.ExpiresAt <= now)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (removed > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired conversation locks", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired locks");
            // Don't throw - this is a background cleanup operation
        }
    }
}

