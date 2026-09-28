using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Compaction;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Mapping;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GuideAntsApi.Services.Conversations.Commands;

/// <summary>
/// Result of a compaction attempt.
/// </summary>
/// <param name="BoundaryTurnIndex">The conversation's current compaction boundary after this call (may be
/// unchanged from before if the press was a no-op). Null if the conversation has never had any complete turn.</param>
/// <param name="MessagesSummarized">Count of raw input messages fed to the compaction engine for turns at or
/// below the boundary — this is <c>CompactionResult.SummarizedMessageCount</c> from the engine, not the number
/// of messages that survived filtering.</param>
/// <param name="EstimatedTokensBefore">Estimated token count of the WHOLE conversation as it currently stands,
/// verbatim - not netted against any existing boundary. If this conversation was already compacted once and
/// has since grown, this number reports the size as if never compacted, not the size actually in effect right
/// now. This is a deliberate simplification (no UI consumer of this field exists yet); a future caller needing
/// the currently-effective size would need to re-run the engine against the previous boundary as well.</param>
/// <param name="EstimatedTokensAfter">Estimated token count if this compaction is applied: the generated
/// summary's character length plus the tail (post-boundary) messages' character length, run through the token
/// estimator.</param>
public sealed record CompactionOutcome(
    int? BoundaryTurnIndex,
    int MessagesSummarized,
    int? EstimatedTokensBefore,
    int? EstimatedTokensAfter);

public interface ICompactionService
{
    Task<CompactionOutcome> CompactConversationAsync(Guid conversationId, CancellationToken ct = default);
}

public sealed class CompactionService : ICompactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDistributedConversationLock _distributedLock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(
        IDistributedConversationLock distributedLock,
        IServiceScopeFactory scopeFactory,
        ILogger<CompactionService> logger)
    {
        _distributedLock = distributedLock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CompactionOutcome> CompactConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, "User", ct);

        if (lockResult.Status == LockAcquisitionStatus.ConversationNotFound)
        {
            throw new KeyNotFoundException("Conversation not found");
        }

        if (lockResult.Status != LockAcquisitionStatus.Acquired)
        {
            // A lock not visible in this process may belong to a worker on another API instance -
            // never infer availability from anything but the distributed lock itself (D8).
            throw new InvalidOperationException(
                $"Conversation is locked by {lockResult.LockedByUserName ?? "another user"}");
        }

        var acquiredLock = lockResult.Lock
            ?? throw new InvalidOperationException("Distributed lock acquisition returned no lease.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var conv = await db.NotebookConversations
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

            if (conv == null)
            {
                throw new KeyNotFoundException("Conversation not found");
            }

            // NOTE: this is max(TurnIndex where Status == "completed"), which only guarantees the boundary
            // TURN ITSELF is complete - it does not guarantee every turn at or below this index is terminal.
            // A turn paused on a client-handled tool (Status == "pending_client_tool") at a LOWER index than
            // a later completed turn will be silently included in the compacted range, since
            // ConversationStreamEngine releases its distributed lock before waiting on the client tool result,
            // letting a later turn complete first. This does not produce an orphaned tool_calls/tool_result
            // pair (both the paused turn and its eventual result share the same pre-boundary index and get
            // replaced together), but it is a real divergence from the design spec's "the boundary stops short
            // of an in-flight turn" language. Tracked for resolution before a later workstream (W5) starts
            // treating "everything at or below the boundary" as always safe to summarize.
            var lastCompleteTurnIndex = await db.ConversationTurns
                .Where(t => t.NotebookConversationId == conversationId && t.Status == "completed")
                .MaxAsync(t => (int?)t.TurnIndex, ct);

            if (lastCompleteTurnIndex == null)
            {
                // Nothing eligible to compact yet. No-op: return the existing boundary unchanged.
                return new CompactionOutcome(conv.CompactionBoundaryTurnIndex, 0, null, null);
            }

            // Monotonic (D8/4.6): only ever move the boundary forward, and only write when it
            // actually changes, so a repeated press with no new complete turn is a true no-op.
            if (!conv.CompactionBoundaryTurnIndex.HasValue
                || lastCompleteTurnIndex.Value > conv.CompactionBoundaryTurnIndex.Value)
            {
                conv.CompactionBoundaryTurnIndex = lastCompleteTurnIndex.Value;
                await db.SaveChangesAsync(ct);
            }

            var boundary = conv.CompactionBoundaryTurnIndex!.Value;
            var (messagesSummarized, tokensBefore, tokensAfter) =
                await ComputeCompactionNumbersAsync(db, conversationId, boundary, ct);

            return new CompactionOutcome(boundary, messagesSummarized, tokensBefore, tokensAfter);
        }
        finally
        {
            await ReleaseLockAsync(conversationId, acquiredLock.LeaseId);
        }
    }

    private async Task<(int MessagesSummarized, int TokensBefore, int TokensAfter)> ComputeCompactionNumbersAsync(
        ApplicationDbContext db, Guid conversationId, int boundaryTurnIndex, CancellationToken ct)
    {
        var allMessages = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId && m.IsStreaming != true)
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .ToListAsync(ct);

        var deduped = ConversationMessageMapper.FilterDuplicateAssistantMessages(allMessages);

        var preBoundary = deduped.Where(m => m.TurnIndex <= boundaryTurnIndex).ToList();
        var tail = deduped.Where(m => m.TurnIndex > boundaryTurnIndex).ToList();

        var preBoundaryChatMessages = preBoundary.Select(ConversationMessageMapper.ToChatMessage).ToList();
        var tailChatMessages = tail.Select(ConversationMessageMapper.ToChatMessage).ToList();
        var allChatMessages = deduped.Select(ConversationMessageMapper.ToChatMessage).ToList();

        var turnFacts = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.TurnIndex <= boundaryTurnIndex)
            .Select(t => new { t.TurnIndex, t.FilesCreated, t.FilesModified })
            .ToListAsync(ct);

        var turnFactsList = turnFacts
            .Select(t => new TurnFacts(t.TurnIndex, ParseFileList(t.FilesCreated), ParseFileList(t.FilesModified)))
            .ToList();

        var compaction = CompactionEngine.Compact(preBoundaryChatMessages, turnFactsList);

        var tokensBefore = PromptTokenEstimator.EstimateTokens(PromptTokenEstimator.CountChars(allChatMessages));
        var tailChars = PromptTokenEstimator.CountChars(tailChatMessages);
        var tokensAfter = PromptTokenEstimator.EstimateTokens(compaction.SummaryText.Length + tailChars);

        return (compaction.SummarizedMessageCount, tokensBefore, tokensAfter);
    }

    private static List<string> ParseFileList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task ReleaseLockAsync(Guid conversationId, Guid leaseId)
    {
        try
        {
            await _distributedLock.ReleaseLockAsync(conversationId, leaseId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Best-effort: an unreleased lease still degrades safely to "wait out the TTL,"
            // never a permanently stuck lock, so a release failure is a warning, not a rethrow.
            _logger.LogWarning(ex, "Failed to release compaction lock for conversation {ConversationId}", conversationId);
        }
    }
}
