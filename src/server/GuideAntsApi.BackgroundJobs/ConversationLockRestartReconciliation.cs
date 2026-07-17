using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.BackgroundJobs;

/// <summary>
/// Conversation stream locks are owned by the API process that acquired them.
/// On restart the holder is gone, so all locks must be cleared immediately.
/// </summary>
public static class ConversationLockRestartReconciliation
{
    public static async Task<int> ClearAllLocksAsync(
        ApplicationDbContext context,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var orphanedLocks = await context.ConversationLocks.ToListAsync(cancellationToken);
        if (orphanedLocks.Count == 0)
        {
            return 0;
        }

        context.ConversationLocks.RemoveRange(orphanedLocks);
        await context.SaveChangesAsync(cancellationToken);

        logger?.LogInformation(
            "Cleared {Count} conversation locks on startup after process restart",
            orphanedLocks.Count);

        return orphanedLocks.Count;
    }
}
