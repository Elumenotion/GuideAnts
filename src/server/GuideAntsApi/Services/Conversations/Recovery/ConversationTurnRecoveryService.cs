using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Services.Conversations.Recovery;

/// <summary>
/// Recovers stale <c>streaming</c> turns that lost their in-process finalizer (crash, host restart, lost client).
/// Must not terminalize turns that still have an active in-process stream worker.
/// </summary>
public sealed class ConversationTurnRecoveryService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationStreamRunRegistry _runRegistry;
    private readonly ILogger<ConversationTurnRecoveryService> _logger;

    public ConversationTurnRecoveryService(
        IServiceScopeFactory scopeFactory,
        ConversationStreamRunRegistry runRegistry,
        ILogger<ConversationTurnRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _runRegistry = runRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RequestPendingCancellationsAsync(stoppingToken);
                await RecoverStaleTurnsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Stale conversation turn recovery sweep failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Delivers durable Stop requests to workers running in this API process. Every API instance
    /// performs the query, so a cancel request routed away from the worker still reaches the
    /// instance that owns its in-process cancellation token.
    /// </summary>
    internal async Task RequestPendingCancellationsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pendingTurnIds = await db.ConversationTurns
            .AsNoTracking()
            // A hard Stop fences the turn immediately, so the durable marker may coexist with
            // Status=cancelled while the remote worker is still physically unwinding.
            .Where(t =>
                t.TerminationCode == "cancel_requested"
                && (t.Status == "streaming" || t.Status == "pending_client_tool"))
            .OrderBy(t => t.LastUpdated)
            .Select(t => t.Id)
            .Take(50)
            .ToListAsync(ct);

        foreach (var turnId in pendingTurnIds)
        {
            if (_runRegistry.RequestCancel(turnId))
            {
                _logger.LogDebug(
                    "Delivered durable cancellation request to active turn {TurnId}",
                    turnId);
            }
        }
    }

    /// <summary>
    /// Contract: skip turns still registered in <see cref="ConversationStreamRunRegistry"/>;
    /// terminalize orphaned streaming turns whose LastUpdated is older than <see cref="StaleAfter"/>.
    /// </summary>
    internal async Task RecoverStaleTurnsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistence = scope.ServiceProvider.GetRequiredService<IConversationPersistence>();
        var privateStreamPolicy = scope.ServiceProvider.GetService<PrivateConversationStreamPolicy>();

        var cutoff = DateTime.UtcNow - StaleAfter;
        var staleTurns = await db.ConversationTurns
            .AsNoTracking()
            .Where(t =>
                t.Status == "streaming"
                && (t.LastUpdated < cutoff || t.TerminationCode == "cancel_requested"))
            .OrderBy(t => t.LastUpdated)
            .Take(20)
            .Select(t => new
            {
                t.Id,
                t.NotebookConversationId,
                t.TurnIndex,
                t.ExecutionId,
                t.TerminationCode
            })
            .ToListAsync(ct);

        foreach (var stale in staleTurns)
        {
            if (_runRegistry.IsInFlight(stale.Id))
            {
                _logger.LogDebug(
                    "Skipping stale-turn recovery for {TurnId}; in-process stream is still in flight",
                    stale.Id);
                continue;
            }

            var hasActiveDistributedLock = await db.ConversationLocks
                .AsNoTracking()
                .AnyAsync(
                    l => l.ConversationId == stale.NotebookConversationId
                         && l.ExpiresAt > DateTime.UtcNow,
                    ct);
            if (hasActiveDistributedLock)
            {
                _logger.LogDebug(
                    "Skipping stale-turn recovery for {TurnId}; distributed conversation lock is still active",
                    stale.Id);
                continue;
            }

            var claimed = stale.TerminationCode == "cancel_requested"
                ? await TryClaimPendingCancellationAsync(db, stale.Id, stale.ExecutionId, ct)
                : await TryClaimStaleTurnAsync(db, stale.Id, stale.ExecutionId, cutoff, ct);
            if (!claimed)
            {
                continue;
            }

            if (privateStreamPolicy != null
                && !privateStreamPolicy.TryReleaseOrphanedConversationGate(stale.NotebookConversationId))
            {
                _logger.LogWarning(
                    "Recovered turn {TurnId}, but the private conversation gate still has a pending acquisition",
                    stale.Id);
            }

            var streamingMessages = await db.NotebookConversationMessages
                .Where(m =>
                    m.NotebookConversationId == stale.NotebookConversationId
                    && m.TurnIndex == stale.TurnIndex
                    && m.Role == DataModel.Models.ChatRole.Assistant
                    && m.IsStreaming == true)
                .ToListAsync(ct);

            foreach (var msg in streamingMessages)
            {
                msg.IsStreaming = false;
            }

            if (streamingMessages.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            try
            {
                await persistence.PruneIncompleteToolCallsAsync(
                    stale.NotebookConversationId,
                    stale.TurnIndex,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to prune incomplete tool calls while recovering turn {TurnId}",
                    stale.Id);
            }

            _logger.LogInformation(
                "Recovered stale streaming turn {TurnId} for conversation {ConversationId} turn index {TurnIndex}",
                stale.Id,
                stale.NotebookConversationId,
                stale.TurnIndex);
        }
    }

    /// <summary>
    /// Atomically claims a stale streaming turn. Returns false when another worker already
    /// terminalized the turn or a concurrent heartbeat advanced <see cref="ConversationTurn.LastUpdated"/>.
    /// </summary>
    internal static async Task<bool> TryClaimStaleTurnAsync(
        ApplicationDbContext db,
        Guid turnId,
        Guid? executionId,
        DateTime cutoff,
        CancellationToken ct)
    {
        var recoveryExecutionId = Guid.NewGuid();
        var claimed = await db.ConversationTurns
            .Where(t =>
                t.Id == turnId
                && t.Status == "streaming"
                && t.LastUpdated < cutoff
                && t.ExecutionId == executionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, "interrupted")
                    .SetProperty(t => t.ExecutionId, recoveryExecutionId)
                    .SetProperty(t => t.TerminalizedAt, DateTime.UtcNow)
                    .SetProperty(t => t.TerminationCode, "stream_interrupted")
                    .SetProperty(t => t.TerminationDetail, "Turn recovered after the stream stopped heartbeating.")
                    .SetProperty(t => t.LastUpdated, DateTime.UtcNow),
                ct);

        return claimed > 0;
    }

    /// <summary>
    /// Claims a durable cancellation after the owning lease is no longer active. This closes the
    /// failure window where the owner disappears after Stop is recorded but before its local
    /// worker can terminalize the turn.
    /// </summary>
    internal static async Task<bool> TryClaimPendingCancellationAsync(
        ApplicationDbContext db,
        Guid turnId,
        Guid? executionId,
        CancellationToken ct)
    {
        var recoveryExecutionId = Guid.NewGuid();
        if (string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            var turn = await db.ConversationTurns
                .FirstOrDefaultAsync(
                    t => t.Id == turnId
                         && t.Status == "streaming"
                         && t.TerminationCode == "cancel_requested"
                         && t.ExecutionId == executionId,
                    ct);
            if (turn == null)
            {
                return false;
            }

            turn.Status = "cancelled";
            turn.ExecutionId = recoveryExecutionId;
            turn.TerminalizedAt = DateTime.UtcNow;
            turn.TerminationCode = "cancelled";
            turn.TerminationDetail = "Turn cancelled by recovery after the stream owner stopped.";
            turn.LastUpdated = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var claimed = await db.ConversationTurns
            .Where(t =>
                t.Id == turnId
                && t.Status == "streaming"
                && t.TerminationCode == "cancel_requested"
                && t.ExecutionId == executionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, "cancelled")
                    .SetProperty(t => t.ExecutionId, recoveryExecutionId)
                    .SetProperty(t => t.TerminalizedAt, DateTime.UtcNow)
                    .SetProperty(t => t.TerminationCode, "cancelled")
                    .SetProperty(t => t.TerminationDetail, "Turn cancelled by recovery after the stream owner stopped.")
                    .SetProperty(t => t.LastUpdated, DateTime.UtcNow),
                ct);

        return claimed > 0;
    }
}
