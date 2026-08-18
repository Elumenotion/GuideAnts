using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Services.Conversations.Recovery;

/// <summary>
/// Recovers stale <c>streaming</c> turns that lost their in-process finalizer (crash, host restart, lost client).
/// </summary>
public sealed class ConversationTurnRecoveryService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationTurnRecoveryService> _logger;

    public ConversationTurnRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationTurnRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

    private async Task RecoverStaleTurnsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistence = scope.ServiceProvider.GetRequiredService<IConversationPersistence>();

        var cutoff = DateTime.UtcNow - StaleAfter;
        var staleTurns = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.Status == "streaming" && t.LastUpdated < cutoff)
            .OrderBy(t => t.LastUpdated)
            .Take(20)
            .Select(t => new
            {
                t.Id,
                t.NotebookConversationId,
                t.TurnIndex,
                t.ExecutionId
            })
            .ToListAsync(ct);

        foreach (var stale in staleTurns)
        {
            var claimed = await db.ConversationTurns
                .Where(t =>
                    t.Id == stale.Id
                    && t.Status == "streaming"
                    && t.LastUpdated < cutoff
                    && t.ExecutionId == stale.ExecutionId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.Status, "interrupted")
                        .SetProperty(t => t.TerminalizedAt, DateTime.UtcNow)
                        .SetProperty(t => t.TerminationCode, "stream_interrupted")
                        .SetProperty(t => t.TerminationDetail, "Turn recovered after the stream stopped heartbeating.")
                        .SetProperty(t => t.LastUpdated, DateTime.UtcNow),
                    ct);

            if (claimed == 0)
            {
                continue;
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
}
