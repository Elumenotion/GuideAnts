using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Database-backed distributed locking service for conversation collaboration.
/// Provides cross-container coordination for single-writer scenarios.
/// </summary>
public class DistributedConversationLockService : IDistributedConversationLock
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DistributedConversationLockService> _logger;

    public DistributedConversationLockService(
        IServiceScopeFactory scopeFactory,
        ILogger<DistributedConversationLockService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ConversationLock?> TryAcquireLockAsync(
        Guid conversationId, 
        string userName,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            // Check for existing lock
            var existingLock = await db.ConversationLocks
                .Where(l => l.ConversationId == conversationId)
                .FirstOrDefaultAsync(cancellationToken);
            
            if (existingLock != null)
            {
                // Check if expired
                if (existingLock.ExpiresAt <= DateTime.UtcNow)
                {
                    // Clean up expired lock
                    db.ConversationLocks.Remove(existingLock);
                    await db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Removed expired lock for conversation {ConversationId}", conversationId);
                }
                else
                {
                    // Lock still active
                    _logger.LogInformation("Conversation {ConversationId} is locked by {LockedByUserName}", 
                        conversationId, existingLock.LockedByUserName);
                    return null;
                }
            }
            
            // Acquire lock
            var newLock = new ConversationLock
            {
                ConversationId = conversationId,
                LockedByUserName = userName,
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            
            db.ConversationLocks.Add(newLock);
            await db.SaveChangesAsync(cancellationToken);
            
            _logger.LogInformation("Acquired lock for conversation {ConversationId} by user {UserName}", 
                conversationId, userName);
            
            return newLock;
        }
        catch (DbUpdateException ex)
        {
            // Race condition - another instance acquired lock simultaneously
            _logger.LogWarning(ex, "Race condition acquiring lock for conversation {ConversationId}", conversationId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring lock for conversation {ConversationId}", conversationId);
            throw;
        }
    }
    
    public async Task ReleaseLockAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var lockToRemove = await db.ConversationLocks
                .Where(l => l.ConversationId == conversationId)
                .FirstOrDefaultAsync(cancellationToken);
            
            if (lockToRemove != null)
            {
                db.ConversationLocks.Remove(lockToRemove);
                await db.SaveChangesAsync(cancellationToken);
                
                _logger.LogInformation("Released lock for conversation {ConversationId}", conversationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing lock for conversation {ConversationId}", conversationId);
            throw;
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
            var expiredLocks = await db.ConversationLocks
                .Where(l => l.ExpiresAt <= DateTime.UtcNow)
                .ToListAsync(cancellationToken);
            
            if (expiredLocks.Any())
            {
                db.ConversationLocks.RemoveRange(expiredLocks);
                await db.SaveChangesAsync(cancellationToken);
                
                _logger.LogInformation("Cleaned up {Count} expired conversation locks", expiredLocks.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired locks");
            // Don't throw - this is a background cleanup operation
        }
    }
}

