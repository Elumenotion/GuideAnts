using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using System.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations.Persistence;

public sealed class ConversationPersistence : IConversationPersistence
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationPersistence> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConversationPersistence(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationPersistence> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CreatedTurnResult> CreateTurnAsync(CreateTurnRequest request, int turnIndex, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dbTurn = new ConversationTurn
        {
            NotebookConversationId = request.ConversationId,
            TurnIndex = turnIndex,
            AssistantName = request.AssistantName,
            ModelDeploymentId = request.ModelDeploymentId,
            Instructions = request.Instructions,
            Created = DateTime.UtcNow,
            Status = request.InitialStatus ?? "completed",
            ExecutionId = request.ExecutionId
                ?? (string.Equals(request.InitialStatus, "streaming", StringComparison.OrdinalIgnoreCase)
                    ? Guid.NewGuid()
                    : null)
        };

        db.ConversationTurns.Add(dbTurn);
        await db.SaveChangesAsync(ct);

        return new CreatedTurnResult(turnIndex, dbTurn.Id, dbTurn);
    }

    public async Task<CreatedTurnResult> CreateNextTurnAsync(CreateTurnRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var turnIndex = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == request.ConversationId)
            .MaxAsync(t => (int?)t.TurnIndex, ct) ?? 0;
        turnIndex++;

        var dbTurn = new ConversationTurn
        {
            NotebookConversationId = request.ConversationId,
            TurnIndex = turnIndex,
            AssistantName = request.AssistantName,
            ModelDeploymentId = request.ModelDeploymentId,
            Instructions = request.Instructions,
            Created = DateTime.UtcNow,
            Status = request.InitialStatus ?? "completed",
            LastUpdated = DateTime.UtcNow,
            ExecutionId = request.ExecutionId
                ?? (string.Equals(request.InitialStatus, "streaming", StringComparison.OrdinalIgnoreCase)
                    ? Guid.NewGuid()
                    : null)
        };

        db.ConversationTurns.Add(dbTurn);
        await db.SaveChangesAsync(ct);

        return new CreatedTurnResult(turnIndex, dbTurn.Id, dbTurn);
    }

    public async Task<CreatedUserMessageResult> CreateUserMessageAsync(CreateUserMessageRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userMessage = new NotebookConversationMessage
        {
            NotebookConversationId = request.ConversationId,
            TurnIndex = request.TurnIndex,
            MessageSequence = request.MessageSequence,
            Role = DataModelChatRole.User,
            Content = request.Content,
            AssistantName = request.AssistantName,
            ModelDeploymentId = request.ModelDeploymentId,
            UserId = request.UserId,
            ExternalUserIdentity = request.ExternalUserIdentity,
            Created = DateTime.UtcNow,
            AssistantId = request.AssistantId
        };

        db.NotebookConversationMessages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        return new CreatedUserMessageResult(userMessage.Id, userMessage);
    }

    public async Task<bool> SetTurnStatusAsync(Guid turnId, string status, string? onlyIfCurrentStatus = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var turn = await db.ConversationTurns.FirstOrDefaultAsync(t => t.Id == turnId, ct);
        if (turn == null)
        {
            return false;
        }

        if (onlyIfCurrentStatus != null
            && !string.Equals(turn.Status, onlyIfCurrentStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        turn.Status = status;
        if (string.Equals(status, "streaming", StringComparison.OrdinalIgnoreCase) && turn.ExecutionId == null)
        {
            turn.ExecutionId = Guid.NewGuid();
        }
        turn.LastUpdated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RequestTurnCancellationAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var requestedAt = DateTime.UtcNow;

        if (string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            var inMemoryTurn = await db.ConversationTurns.FirstOrDefaultAsync(
                t => t.Id == turnId
                     && t.NotebookConversationId == conversationId
                     && t.Status == "streaming",
                ct);
            if (inMemoryTurn == null)
            {
                return false;
            }

            inMemoryTurn.TerminationCode = "cancel_requested";
            inMemoryTurn.TerminationDetail = "Stop was requested by the user.";
            inMemoryTurn.LastUpdated = requestedAt;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var updated = await db.ConversationTurns
            .Where(t =>
                t.Id == turnId
                && t.NotebookConversationId == conversationId
                && t.Status == "streaming")
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.TerminationCode, "cancel_requested")
                    .SetProperty(t => t.TerminationDetail, "Stop was requested by the user.")
                    .SetProperty(t => t.LastUpdated, requestedAt),
                ct);

        return updated > 0;
    }

    public async Task<FencedTurnCancellationResult> FenceTurnCancellationAsync(
        Guid conversationId,
        Guid turnId,
        Guid? expectedExecutionId = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await ExecuteReadCommittedWriteAsync<FencedTurnCancellationResult>(db, async () =>
        {
            var turn = await db.ConversationTurns
                .FirstOrDefaultAsync(
                    t => t.Id == turnId && t.NotebookConversationId == conversationId,
                    ct);
            if (turn == null)
            {
                return new FencedTurnCancellationResult(
                    Found: false,
                    WasStreaming: false,
                    PreviousExecutionId: null,
                    FencedExecutionId: null,
                    PreviousLeaseWasReleased: false,
                    Status: null);
            }

            var wasStreaming = string.Equals(
                turn.Status,
                "streaming",
                StringComparison.OrdinalIgnoreCase);
            var wasPendingClientTool = string.Equals(
                turn.Status,
                "pending_client_tool",
                StringComparison.OrdinalIgnoreCase);
            var wasCancellable = wasStreaming || wasPendingClientTool;
            var cancellationTerminalizationWon =
                expectedExecutionId.HasValue
                && turn.ExecutionId == expectedExecutionId
                && string.Equals(turn.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(turn.TerminationCode, "cancelled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(turn.TerminationCode, "cancel_requested", StringComparison.OrdinalIgnoreCase));
            var shouldAdvanceFence = wasCancellable || cancellationTerminalizationWon;
            var conversationLock = await db.ConversationLocks
                .FirstOrDefaultAsync(l => l.ConversationId == conversationId, ct);
            if (conversationLock != null && conversationLock.ExpiresAt <= DateTime.UtcNow)
            {
                // An expired row is no longer an owner's authority. Remove it in this same
                // serializable operation so an abandoned lease cannot make Stop fail closed
                // forever while still blocking the next acquisition.
                db.ConversationLocks.Remove(conversationLock);
                conversationLock = null;
            }

            if (expectedExecutionId.HasValue && turn.ExecutionId != expectedExecutionId)
            {
                // A stale worker may ask for cancellation after a replacement execution has
                // claimed the turn. It cannot fence that newer generation or release its lease.
                var staleWorkerLeaseConflict = conversationLock != null
                    && (!turn.ExecutionId.HasValue
                        || conversationLock.LeaseId != turn.ExecutionId.Value);
                await db.SaveChangesAsync(ct);
                return new FencedTurnCancellationResult(
                    Found: true,
                    WasStreaming: false,
                    PreviousExecutionId: turn.ExecutionId,
                    FencedExecutionId: turn.ExecutionId,
                    PreviousLeaseWasReleased: false,
                    Status: turn.Status,
                    ConflictingLeasePresent: staleWorkerLeaseConflict);
            }

            if (conversationLock != null
                && turn.ExecutionId.HasValue
                && conversationLock.LeaseId != turn.ExecutionId.Value)
            {
                // Never cancel an older turn through a lease that belongs to a replacement
                // execution. The caller must fail closed while leaving the newer lifecycle
                // untouched.
                return new FencedTurnCancellationResult(
                    Found: true,
                    WasStreaming: wasStreaming,
                    PreviousExecutionId: turn.ExecutionId,
                    FencedExecutionId: turn.ExecutionId,
                    PreviousLeaseWasReleased: false,
                    Status: turn.Status,
                    ConflictingLeasePresent: true,
                    WasPendingClientTool: wasPendingClientTool);
            }

            // The initial stream execution uses the distributed lease as its execution fence.
            // Keep that identity long enough to remove only the old owner's lock below. A Stop
            // against an already-terminal turn must never infer ownership from the current lock.
            var previousExecutionId = shouldAdvanceFence
                ? turn.ExecutionId ?? conversationLock?.LeaseId
                : turn.ExecutionId;
            var fencedExecutionId = turn.ExecutionId;

            if (shouldAdvanceFence)
            {
                fencedExecutionId = Guid.NewGuid();
                var now = DateTime.UtcNow;

                if (wasCancellable)
                {
                    turn.Status = "cancelled";
                    turn.TerminalizedAt = now;
                    // Keep the durable marker distinguishable from worker cleanup. Recovery uses it
                    // to signal a remote in-process worker that has not seen the Stop request.
                    turn.TerminationCode = "cancel_requested";
                    turn.TerminationDetail = "Stream was cancelled by user.";
                }

                turn.ExecutionId = fencedExecutionId;
                turn.LastUpdated = now;

                // Preserve all accumulated content. Only clear the presentation flag so a
                // cancelled turn cannot continue to appear as a live preview.
                var streamingAssistantMessages = await db.NotebookConversationMessages
                    .Where(m =>
                        m.NotebookConversationId == conversationId
                        && m.TurnIndex == turn.TurnIndex
                        && m.Role == DataModelChatRole.Assistant
                        && m.IsStreaming == true)
                    .ToListAsync(ct);

                foreach (var message in streamingAssistantMessages)
                {
                    message.IsStreaming = false;
                }

                var materialized = await MaterializeMissingCancellationToolResultsInContextAsync(
                    db,
                    conversationId,
                    turn,
                    now,
                    ct);
                if (materialized > 0)
                {
                    _logger.LogInformation(
                        "Fenced turn {TurnId} in conversation {ConversationId} with {MaterializedCount} cancellation tool result(s)",
                        turn.Id,
                        conversationId,
                        materialized);
                }
            }

            var previousLeaseWasReleased = false;
            if (previousExecutionId.HasValue
                && conversationLock?.LeaseId == previousExecutionId.Value)
            {
                // The worker can release its lease concurrently after observing the hard-stop
                // signal. Use a fenced set-based delete instead of deleting a tracked entity so
                // that an already-removed old lease is treated as success rather than producing
                // DbUpdateConcurrencyException. A replacement lease can never match this
                // predicate.
                if (string.Equals(
                        db.Database.ProviderName,
                        "Microsoft.EntityFrameworkCore.InMemory",
                        StringComparison.Ordinal))
                {
                    db.ConversationLocks.Remove(conversationLock);
                    previousLeaseWasReleased = true;
                }
                else
                {
                    previousLeaseWasReleased = await db.ConversationLocks
                        .Where(l =>
                            l.ConversationId == conversationId
                            && l.LeaseId == previousExecutionId.Value)
                        .ExecuteDeleteAsync(ct) > 0;
                }
                conversationLock = null;
            }
            var conflictingLeasePresent = conversationLock != null
                && (!previousExecutionId.HasValue
                    || conversationLock.LeaseId != previousExecutionId.Value);

            await db.SaveChangesAsync(ct);

            return new FencedTurnCancellationResult(
                Found: true,
                WasStreaming: wasStreaming,
                PreviousExecutionId: previousExecutionId,
                FencedExecutionId: fencedExecutionId,
                PreviousLeaseWasReleased: previousLeaseWasReleased,
                Status: turn.Status,
                ConflictingLeasePresent: conflictingLeasePresent,
                WasPendingClientTool: wasPendingClientTool);
        }, ct);
    }

    public async Task<int> MaterializeMissingCancellationToolResultsAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await ExecuteReadCommittedWriteAsync(db, async () =>
        {
            var turn = await db.ConversationTurns
                .FirstOrDefaultAsync(
                    t => t.Id == turnId && t.NotebookConversationId == conversationId,
                    ct);
            if (turn == null)
            {
                return 0;
            }

            var materialized = await MaterializeMissingCancellationToolResultsInContextAsync(
                db,
                conversationId,
                turn,
                DateTime.UtcNow,
                ct);
            if (materialized > 0)
            {
                _logger.LogInformation(
                    "Materialized {MaterializedCount} cancellation tool result(s) for turn {TurnId} in conversation {ConversationId}",
                    materialized,
                    turnId,
                    conversationId);
                await db.SaveChangesAsync(ct);
            }

            return materialized;
        }, ct);
    }

    public async Task<bool> TryPreserveStoppedAssistantToolCallsAsync(
        Guid conversationId,
        Guid turnId,
        Guid? messageId,
        string? content,
        string toolCallsJson,
        Guid? assistantId = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await ExecuteSerializableWriteAsync(db, async () =>
        {
            var turn = await db.ConversationTurns
                .FirstOrDefaultAsync(
                    t => t.Id == turnId && t.NotebookConversationId == conversationId,
                    ct);
            if (turn == null)
            {
                return false;
            }

            var assistantMessages = await db.NotebookConversationMessages
                .Where(m =>
                    m.NotebookConversationId == conversationId
                    && m.TurnIndex == turn.TurnIndex
                    && m.Role == DataModelChatRole.Assistant)
                .OrderBy(m => m.MessageSequence)
                .ThenBy(m => m.Created)
                .ToListAsync(ct);

            if (assistantMessages.Any(m =>
                    string.Equals(m.ToolCalls, toolCallsJson, StringComparison.Ordinal)))
            {
                return false;
            }

            var message = messageId.HasValue
                ? assistantMessages.FirstOrDefault(m => m.Id == messageId.Value)
                : null;
            if (message == null || !string.IsNullOrWhiteSpace(message.ToolCalls))
            {
                var nextMessageSequence = await db.NotebookConversationMessages
                    .Where(m =>
                        m.NotebookConversationId == conversationId
                        && m.TurnIndex == turn.TurnIndex)
                    .MaxAsync(m => (int?)m.MessageSequence, ct) ?? 0;
                var preservedMessage = new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = turn.TurnIndex,
                    MessageSequence = nextMessageSequence + 1,
                    Role = DataModelChatRole.Assistant,
                    Content = content ?? string.Empty,
                    AssistantName = turn.AssistantName,
                    ModelDeploymentId = turn.ModelDeploymentId,
                    ToolCalls = toolCallsJson,
                    IsStreaming = false,
                    Created = DateTime.UtcNow,
                    AssistantId = assistantId
                };
                db.NotebookConversationMessages.Add(preservedMessage);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(message.Content))
                {
                    message.Content = content;
                }

                message.ToolCalls = toolCallsJson;
                message.IsStreaming = false;
            }

            turn.LastUpdated = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    private static async Task<int> MaterializeMissingCancellationToolResultsInContextAsync(
        ApplicationDbContext db,
        Guid conversationId,
        ConversationTurn turn,
        DateTime now,
        CancellationToken ct)
    {
        var turnMessages = await db.NotebookConversationMessages
            .Where(m =>
                m.NotebookConversationId == conversationId
                && m.TurnIndex == turn.TurnIndex)
            .ToListAsync(ct);
        var toolResultIds = new HashSet<string>(
            turnMessages
                .Where(m =>
                    m.Role == DataModelChatRole.Tool
                    && !string.IsNullOrWhiteSpace(m.ToolCallId))
                .Select(m => m.ToolCallId!),
            StringComparer.Ordinal);
        var nextMessageSequence = turnMessages.Count == 0
            ? 1
            : turnMessages.Max(m => m.MessageSequence) + 1;
        var materialized = 0;

        foreach (var assistantMessage in turnMessages.Where(m =>
                     m.Role == DataModelChatRole.Assistant
                     && !string.IsNullOrWhiteSpace(m.ToolCalls)))
        {
            List<ChatToolCall>? toolCalls;
            try
            {
                toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(
                    assistantMessage.ToolCalls!,
                    JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (toolCalls == null)
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                if (!toolCall.IsFunction
                    || string.IsNullOrWhiteSpace(toolCall.Id)
                    || !toolResultIds.Add(toolCall.Id))
                {
                    continue;
                }

                db.NotebookConversationMessages.Add(new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = turn.TurnIndex,
                    MessageSequence = nextMessageSequence++,
                    Role = DataModelChatRole.Tool,
                    Content = "ERROR: Operation was cancelled",
                    ToolCallId = toolCall.Id,
                    FunctionName = toolCall.Function.Name,
                    IsStreaming = false,
                    Created = now,
                    AssistantId = assistantMessage.AssistantId,
                    AssistantName = assistantMessage.AssistantName
                });
                materialized++;
            }
        }

        if (materialized > 0)
        {
            turn.LastUpdated = now;
        }

        return materialized;
    }

    public async Task<bool> IsTurnCancellationRequestedAsync(
        Guid turnId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ConversationTurns
            .AsNoTracking()
            .AnyAsync(
                t => t.Id == turnId
                    && (t.TerminationCode == "cancel_requested"
                        || (t.Status == "cancelled" && t.TerminationCode == "cancelled")),
                ct);
    }

    public async Task<bool> IsTurnExecutionActiveAsync(
        Guid turnId,
        Guid expectedExecutionId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ConversationTurns
            .AsNoTracking()
            .AnyAsync(
                t => t.Id == turnId
                    && (t.Status == "streaming" || t.Status == "pending_client_tool")
                    && t.ExecutionId == expectedExecutionId,
                ct);
    }

    public async Task<Guid> StartAssistantMessageAsync(StartAssistantMessageRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await ExecuteAtomicFencedWriteAsync<Guid>(
            db,
            request.TurnId,
            request.ExpectedExecutionId,
            async () =>
            {
                var turn = await LoadWritableTurnForWriteAsync(
                    db,
                    request.TurnId,
                    request.ExpectedExecutionId,
                    ct);
                if (turn == null)
                {
                    throw new KeyNotFoundException($"Conversation turn {request.TurnId} was not found.");
                }

                var msg = new NotebookConversationMessage
                {
                    NotebookConversationId = request.ConversationId,
                    TurnIndex = request.TurnIndex,
                    MessageSequence = request.MessageSequence,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = request.AssistantName,
                    ModelDeploymentId = request.ModelDeploymentId,
                    Content = request.Content,
                    ToolCalls = request.ToolCallsJson,
                    IsStreaming = request.IsStreaming,
                    Created = DateTime.UtcNow,
                    AssistantId = request.AssistantId
                };

                db.NotebookConversationMessages.Add(msg);
                TouchTurn(turn);
                await db.SaveChangesAsync(ct);
                return msg.Id;
            },
            ct);
    }

    public async Task AppendOrFinalizeAssistantMessageAsync(AssistantMessageUpdateRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await ExecuteAtomicFencedWriteAsync(
            db,
            request.TurnId,
            request.ExpectedExecutionId,
            async () =>
            {
                var turn = await LoadWritableTurnForWriteAsync(
                    db,
                    request.TurnId,
                    request.ExpectedExecutionId,
                    ct);
                if (turn == null)
                {
                    throw new KeyNotFoundException($"Conversation turn {request.TurnId} was not found.");
                }

                var stub = new NotebookConversationMessage { Id = request.MessageId };
                db.Attach(stub);
                stub.Content = request.Content;
                db.Entry(stub).Property(x => x.Content).IsModified = true;

                if (request.Finalize)
                {
                    stub.IsStreaming = false;
                    db.Entry(stub).Property(x => x.IsStreaming).IsModified = true;

                    if (request.ToolCallsJson != null)
                    {
                        stub.ToolCalls = request.ToolCallsJson;
                        db.Entry(stub).Property(x => x.ToolCalls).IsModified = true;
                    }
                }

                if (request.ThinkingBlocksJson != null)
                {
                    stub.ThinkingBlocksJson = request.ThinkingBlocksJson;
                    db.Entry(stub).Property(x => x.ThinkingBlocksJson).IsModified = true;
                }

                TouchTurn(turn);
                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public async Task FinalizeStreamingAssistantMessageIfStillStreamingAsync(
        Guid messageId,
        Guid turnId,
        string content,
        CancellationToken ct = default,
        Guid? expectedExecutionId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await ExecuteAtomicFencedWriteAsync(
            db,
            turnId,
            expectedExecutionId,
            async () =>
            {
                var turn = await LoadWritableTurnForWriteAsync(db, turnId, expectedExecutionId, ct);
                if (turn == null)
                {
                    return;
                }

                var existingMsg = await db.NotebookConversationMessages
                    .Where(m => m.Id == messageId)
                    .Select(m => new { m.IsStreaming })
                    .FirstOrDefaultAsync(ct);

                if (existingMsg == null || !(existingMsg.IsStreaming ?? false))
                {
                    return;
                }

                var stub = new NotebookConversationMessage { Id = messageId };
                db.Attach(stub);
                stub.Content = content;
                stub.IsStreaming = false;
                db.Entry(stub).Property(x => x.Content).IsModified = true;
                db.Entry(stub).Property(x => x.IsStreaming).IsModified = true;

                TouchTurn(turn);
                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public async Task<CreateToolMessageResult> CreateToolMessageAsync(
        CreateToolMessageRequest request,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await ExecuteAtomicFencedWriteAsync<CreateToolMessageResult>(
            db,
            request.TurnId,
            request.ExpectedExecutionId,
            async () =>
            {
                var turn = await LoadWritableTurnForWriteAsync(
                    db,
                    request.TurnId,
                    request.ExpectedExecutionId,
                    ct);
                if (turn == null)
                {
                    throw new KeyNotFoundException($"Conversation turn {request.TurnId} was not found.");
                }

                // One tool result per ToolCallId. Context-overflow unwind (and retries) must update
                // in place — inserting a second row with the same id breaks history rebuild.
                if (!string.IsNullOrWhiteSpace(request.ToolCallId))
                {
                    var existingForCall = await db.NotebookConversationMessages
                        .Where(m =>
                            m.NotebookConversationId == request.ConversationId &&
                            m.Role == DataModelChatRole.Tool &&
                            m.ToolCallId == request.ToolCallId)
                        .OrderBy(m => m.MessageSequence)
                        .ThenBy(m => m.Created)
                        .ToListAsync(ct);

                    if (existingForCall.Count > 0)
                    {
                        var keep = existingForCall[0];
                        keep.Content = request.Content;
                        if (!string.IsNullOrEmpty(request.FunctionName))
                        {
                            keep.FunctionName = request.FunctionName;
                        }

                        if (request.AssistantId.HasValue)
                        {
                            keep.AssistantId = request.AssistantId;
                        }

                        if (!string.IsNullOrEmpty(request.AssistantName))
                        {
                            keep.AssistantName = request.AssistantName;
                        }

                        keep.IsStreaming = false;

                        for (var i = 1; i < existingForCall.Count; i++)
                        {
                            db.NotebookConversationMessages.Remove(existingForCall[i]);
                        }

                        TouchTurn(turn);
                        await db.SaveChangesAsync(ct);
                        return new CreateToolMessageResult(keep.Id, Created: false);
                    }
                }

                var toolMessage = new NotebookConversationMessage
                {
                    NotebookConversationId = request.ConversationId,
                    TurnIndex = request.TurnIndex,
                    MessageSequence = request.MessageSequence,
                    Role = DataModelChatRole.Tool,
                    Content = request.Content,
                    ToolCallId = request.ToolCallId,
                    FunctionName = request.FunctionName,
                    IsStreaming = false,
                    Created = DateTime.UtcNow,
                    AssistantId = request.AssistantId,
                    AssistantName = request.AssistantName
                };

                db.NotebookConversationMessages.Add(toolMessage);
                TouchTurn(turn);
                await db.SaveChangesAsync(ct);
                return new CreateToolMessageResult(toolMessage.Id, Created: true);
            },
            ct);
    }

    public async Task PersistRunOutputAsync(Guid turnId, ChatRunOutput? output, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var turn = await db.ConversationTurns.FirstOrDefaultAsync(t => t.Id == turnId, ct);
        if (turn == null)
        {
            return;
        }

        turn.ChatRunOutputJson = output != null ? JsonSerializer.Serialize(output, JsonOptions) : null;
        turn.UsageJson = output?.Usage != null ? JsonSerializer.Serialize(output.Usage, JsonOptions) : null;

        if (output?.NewFiles is { Count: > 0 })
        {
            turn.FilesCreated = JsonSerializer.Serialize(output.NewFiles, JsonOptions);
        }

        if (output?.ModifiedFiles is { Count: > 0 })
        {
            turn.FilesModified = JsonSerializer.Serialize(output.ModifiedFiles, JsonOptions);
        }

        turn.LastUpdated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task PruneIncompleteToolCallsAsync(Guid conversationId, int turnIndex, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (await PruneIncompleteToolCallsInContextAsync(db, conversationId, turnIndex, ct))
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task PersistThinkingBlocksAsync(
        ChatRunOutput? output,
        IReadOnlyList<Guid> assistantMessageIds,
        CancellationToken ct = default)
    {
        if (output?.Messages == null || assistantMessageIds.Count == 0)
        {
            return;
        }

        var assistantMessages = output.Messages
            .Where(m => m.Role == ChatMessageRole.Assistant)
            .ToList();

        if (assistantMessages.Count < assistantMessageIds.Count)
        {
            return;
        }

        var recentAssistantMessages = assistantMessages
            .Skip(assistantMessages.Count - assistantMessageIds.Count)
            .ToList();

        var updates = new List<(Guid Id, string ThinkingJson)>();
        for (var i = 0; i < assistantMessageIds.Count; i++)
        {
            var thinkingBlocks = recentAssistantMessages[i].ThinkingBlocks;
            if (thinkingBlocks is not { Count: > 0 })
            {
                continue;
            }

            var json = JsonSerializer.Serialize(thinkingBlocks, JsonOptions);
            updates.Add((assistantMessageIds[i], json));
        }

        if (updates.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var update in updates)
        {
            var stub = new NotebookConversationMessage { Id = update.Id };
            db.Attach(stub);
            stub.ThinkingBlocksJson = update.ThinkingJson;
            db.Entry(stub).Property(x => x.ThinkingBlocksJson).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AppendTurnTraceSegmentAsync(AppendTurnTraceSegmentRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SegmentJson))
        {
            throw new InvalidOperationException("Trace segment JSON cannot be empty.");
        }

        var segmentNode = JsonNode.Parse(request.SegmentJson)
            ?? throw new InvalidOperationException("Trace segment JSON is invalid.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // SaveChanges owns the atomic trace mutation. When an execution fence is supplied, mark
        // the unchanged fence value as modified so EF includes the concurrency-token predicate in
        // the same implicit transaction without opening a MARS-incompatible user transaction.
        async Task AppendTraceAsync()
        {
            // Undo / orphan cancel can delete the turn while a background worker still holds its id.
            // Never FK-crash stream finalization over trace housekeeping.
            var turn = await db.ConversationTurns
                .AsNoTracking()
                .Where(t => t.Id == request.TurnId)
                .Select(t => new { t.Status, t.ExecutionId })
                .FirstOrDefaultAsync(ct);
            if (turn == null)
            {
                _logger.LogWarning(
                    "Skipping turn-trace append for missing turn {TurnId} (conversation {ConversationId}, turnIndex {TurnIndex}, state {CaptureState})",
                    request.TurnId,
                    request.ConversationId,
                    request.TurnIndex,
                    request.CaptureState);
                return;
            }

            if (request.ExpectedExecutionId.HasValue
                && turn.ExecutionId != request.ExpectedExecutionId)
            {
                _logger.LogWarning(
                    "Skipping stale turn-trace append for turn {TurnId}; expected execution {ExpectedExecutionId}, current {CurrentExecutionId}",
                    request.TurnId,
                    request.ExpectedExecutionId,
                    turn.ExecutionId);
                return;
            }

            if (request.ExpectedExecutionId.HasValue)
            {
                var turnFence = new ConversationTurn
                {
                    Id = request.TurnId,
                    Status = turn.Status,
                    ExecutionId = turn.ExecutionId
                };
                db.Attach(turnFence);
                db.Entry(turnFence).Property(t => t.ExecutionId).IsModified = true;
            }

            var trace = await db.ConversationTurnTraces
                .FirstOrDefaultAsync(t => t.ConversationTurnId == request.TurnId, ct);

            if (trace == null)
            {
                var envelope = new JsonObject
                {
                    ["schemaVersion"] = request.SchemaVersion,
                    ["segments"] = new JsonArray(segmentNode)
                };

                db.ConversationTurnTraces.Add(new ConversationTurnTrace
                {
                    ConversationTurnId = request.TurnId,
                    NotebookConversationId = request.ConversationId,
                    TurnIndex = request.TurnIndex,
                    SchemaVersion = request.SchemaVersion,
                    CaptureState = request.CaptureState,
                    TraceJson = envelope.ToJsonString(JsonOptions),
                    Created = DateTime.UtcNow,
                    Updated = DateTime.UtcNow
                });

                await db.SaveChangesAsync(ct);
                return;
            }

            JsonObject envelopeNode;
            if (string.IsNullOrWhiteSpace(trace.TraceJson))
            {
                envelopeNode = new JsonObject();
            }
            else
            {
                envelopeNode = JsonNode.Parse(trace.TraceJson) as JsonObject
                    ?? throw new InvalidOperationException(
                        $"Stored turn trace payload for turn {request.TurnId} is malformed.");
            }

            envelopeNode["schemaVersion"] = request.SchemaVersion;
            var segments = envelopeNode["segments"] as JsonArray;
            if (segments == null)
            {
                segments = new JsonArray();
                envelopeNode["segments"] = segments;
            }

            segments.Add(segmentNode);

            trace.SchemaVersion = request.SchemaVersion;
            trace.CaptureState = request.CaptureState;
            trace.TraceJson = envelopeNode.ToJsonString(JsonOptions);
            trace.Updated = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        await ExecuteAtomicFencedWriteAsync(
            db,
            request.TurnId,
            request.ExpectedExecutionId,
            AppendTraceAsync,
            ct);
    }

    private static async Task ExecuteSerializableWriteAsync(
        ApplicationDbContext db,
        Func<Task> operation,
        CancellationToken ct)
    {
        await ExecuteWriteAsync(db, operation, IsolationLevel.Serializable, ct);
    }

    private static async Task<T> ExecuteSerializableWriteAsync<T>(
        ApplicationDbContext db,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        return await ExecuteWriteAsync(db, operation, IsolationLevel.Serializable, ct);
    }

    private static async Task<T> ExecuteReadCommittedWriteAsync<T>(
        ApplicationDbContext db,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        return await ExecuteWriteAsync(db, operation, IsolationLevel.ReadCommitted, ct);
    }

    private static async Task ExecuteAtomicFencedWriteAsync(
        ApplicationDbContext db,
        Guid turnId,
        Guid? expectedExecutionId,
        Func<Task> operation,
        CancellationToken ct)
    {
        // SaveChanges owns the atomic message-plus-turn write. Status and ExecutionId are
        // concurrency predicates, so a fence change between the read and save rolls back the
        // entire implicit transaction without creating a MARS-disabled savepoint warning.
        try
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteImplicitWriteAsync(db, operation, ct);
        }
        catch (DbUpdateConcurrencyException) when (expectedExecutionId.HasValue)
        {
            throw new ConversationTurnExecutionFencedException(turnId);
        }
    }

    private static async Task<T> ExecuteAtomicFencedWriteAsync<T>(
        ApplicationDbContext db,
        Guid turnId,
        Guid? expectedExecutionId,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await ExecuteImplicitWriteAsync(db, operation, ct);
        }
        catch (DbUpdateConcurrencyException) when (expectedExecutionId.HasValue)
        {
            throw new ConversationTurnExecutionFencedException(turnId);
        }
    }

    private static async Task ExecuteImplicitWriteAsync(
        ApplicationDbContext db,
        Func<Task> operation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            try
            {
                await operation();
            }
            catch
            {
                // SaveChanges owns the implicit transaction. Clear tracked state before an
                // execution-strategy retry or before the caller observes a failed write.
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static async Task<T> ExecuteImplicitWriteAsync<T>(
        ApplicationDbContext db,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            try
            {
                return await operation();
            }
            catch
            {
                // SaveChanges owns the implicit transaction. Clear tracked state before an
                // execution-strategy retry or before the caller observes a failed write.
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static async Task ExecuteWriteAsync(
        ApplicationDbContext db,
        Func<Task> operation,
        IsolationLevel isolationLevel,
        CancellationToken ct)
    {
        if (string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            await operation();
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var autoSavepointsEnabled = db.Database.AutoSavepointsEnabled;
            db.Database.AutoSavepointsEnabled = false;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(
                    isolationLevel,
                    ct);
                try
                {
                    await operation();
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    finally
                    {
                        // MARS disables EF's automatic savepoints. Always detach the failed
                        // attempt, even if rollback itself reports a connection failure.
                        db.ChangeTracker.Clear();
                    }

                    throw;
                }
            }
            finally
            {
                // The transaction helper owns rollback, so automatic savepoints are unnecessary
                // and would emit SavepointsDisabledBecauseOfMARS on every SaveChanges call.
                db.Database.AutoSavepointsEnabled = autoSavepointsEnabled;
            }
        });
    }

    private static async Task<T> ExecuteWriteAsync<T>(
        ApplicationDbContext db,
        Func<Task<T>> operation,
        IsolationLevel isolationLevel,
        CancellationToken ct)
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
            var autoSavepointsEnabled = db.Database.AutoSavepointsEnabled;
            db.Database.AutoSavepointsEnabled = false;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(
                    isolationLevel,
                    ct);
                try
                {
                    var result = await operation();
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    finally
                    {
                        // MARS disables EF's automatic savepoints. Always detach the failed
                        // attempt, even if rollback itself reports a connection failure.
                        db.ChangeTracker.Clear();
                    }

                    throw;
                }
            }
            finally
            {
                // The transaction helper owns rollback, so automatic savepoints are unnecessary
                // and would emit SavepointsDisabledBecauseOfMARS on every SaveChanges call.
                db.Database.AutoSavepointsEnabled = autoSavepointsEnabled;
            }
        });
    }

    private static async Task<ConversationTurn?> LoadWritableTurnForWriteAsync(
        ApplicationDbContext db,
        Guid turnId,
        Guid? expectedExecutionId,
        CancellationToken ct)
    {
        var snapshot = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.Id == turnId)
            .Select(t => new { t.Status, t.ExecutionId })
            .FirstOrDefaultAsync(ct);

        if (snapshot == null)
        {
            if (expectedExecutionId.HasValue)
            {
                throw new ConversationTurnExecutionFencedException(turnId);
            }

            return null;
        }

        if (expectedExecutionId.HasValue
            && ((!string.Equals(snapshot.Status, "streaming", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(snapshot.Status, "pending_client_tool", StringComparison.OrdinalIgnoreCase))
                || snapshot.ExecutionId != expectedExecutionId))
        {
            throw new ConversationTurnExecutionFencedException(turnId);
        }

        var turn = db.ConversationTurns.Local.FirstOrDefault(t => t.Id == turnId);
        if (turn == null)
        {
            turn = new ConversationTurn
            {
                Id = turnId,
                Status = snapshot.Status,
                ExecutionId = snapshot.ExecutionId
            };
            db.Attach(turn);
        }

        var entry = db.Entry(turn);
        entry.Property(t => t.Status).OriginalValue = snapshot.Status;
        entry.Property(t => t.Status).IsModified = false;
        entry.Property(t => t.ExecutionId).OriginalValue = snapshot.ExecutionId;
        entry.Property(t => t.ExecutionId).IsModified = false;
        return turn;
    }

    private static void TouchTurn(ConversationTurn turn)
    {
        // The turn was attached with its current lifecycle values, so SaveChanges can use those
        // values as optimistic fence predicates without loading the large output columns.
        turn.LastUpdated = DateTime.UtcNow;
    }

    private static bool IsTerminalTurnStatus(string status) =>
        !string.Equals(status, "streaming", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, "pending_client_tool", StringComparison.OrdinalIgnoreCase);

    private static string? BoundTerminationDetail(string? detail) =>
        detail == null ? null : detail.Length <= 500 ? detail : detail[..500];

    public async Task<bool> TerminalizeTurnAsync(TerminalizeTurnRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        async Task<bool> TerminalizeAsync()
        {
            try
            {
                var turn = await db.ConversationTurns.FirstOrDefaultAsync(t => t.Id == request.TurnId, ct);
                if (turn == null)
                {
                    return false;
                }

                var alreadyTerminal = IsTerminalTurnStatus(turn.Status);
                if (!alreadyTerminal
                    && request.ExecutionId.HasValue
                    && turn.ExecutionId != request.ExecutionId)
                {
                    return false;
                }
                if (alreadyTerminal
                    && request.ExecutionId.HasValue
                    && turn.ExecutionId != request.ExecutionId)
                {
                    // A recovery claim advanced the execution fence. An old worker may still
                    // reach this idempotent terminalization call, but it must not overwrite the
                    // recovered turn's messages or metadata.
                    return false;
                }

                if (request.AssistantSnapshots is { Count: > 0 })
                {
                    foreach (var snapshot in request.AssistantSnapshots)
                    {
                        var msg = await db.NotebookConversationMessages
                            .FirstOrDefaultAsync(m => m.Id == snapshot.MessageId, ct);
                        if (msg == null)
                        {
                            continue;
                        }

                        msg.Content = snapshot.Content;
                        if (snapshot.ToolCallsJson != null)
                        {
                            msg.ToolCalls = snapshot.ToolCallsJson;
                        }

                        if (snapshot.ThinkingBlocksJson != null)
                        {
                            msg.ThinkingBlocksJson = snapshot.ThinkingBlocksJson;
                        }

                        msg.IsStreaming = false;
                    }
                }

                var streamingAssistantMessages = await db.NotebookConversationMessages
                    .Where(m =>
                        m.NotebookConversationId == request.ConversationId
                        && m.TurnIndex == request.TurnIndex
                        && m.Role == DataModelChatRole.Assistant
                        && m.IsStreaming == true)
                    .ToListAsync(ct);

                foreach (var msg in streamingAssistantMessages)
                {
                    msg.IsStreaming = false;
                }

                if (request.Output?.Messages != null && request.AssistantMessageIdsForThinking is { Count: > 0 })
                {
                    var assistantMessages = request.Output.Messages
                        .Where(m => m.Role == ChatMessageRole.Assistant)
                        .ToList();

                    if (assistantMessages.Count >= request.AssistantMessageIdsForThinking.Count)
                    {
                        var recentAssistantMessages = assistantMessages
                            .Skip(assistantMessages.Count - request.AssistantMessageIdsForThinking.Count)
                            .ToList();

                        for (var i = 0; i < request.AssistantMessageIdsForThinking.Count; i++)
                        {
                            var thinkingBlocks = recentAssistantMessages[i].ThinkingBlocks;
                            if (thinkingBlocks is not { Count: > 0 })
                            {
                                continue;
                            }

                            var messageId = request.AssistantMessageIdsForThinking[i];
                            var msg = await db.NotebookConversationMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
                            if (msg == null)
                            {
                                continue;
                            }

                            msg.ThinkingBlocksJson = JsonSerializer.Serialize(thinkingBlocks, JsonOptions);
                        }
                    }
                }

                if (request.PruneIncompleteToolCalls)
                {
                    await PruneIncompleteToolCallsInContextAsync(
                        db,
                        request.ConversationId,
                        request.TurnIndex,
                        ct);
                }

                if (!alreadyTerminal)
                {
                    var cancellationWasRequested =
                        !string.Equals(request.TerminalStatus, "cancelled", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(turn.TerminationCode, "cancel_requested", StringComparison.OrdinalIgnoreCase);
                    turn.Status = cancellationWasRequested ? "cancelled" : request.TerminalStatus;
                    turn.TerminalizedAt = DateTime.UtcNow;
                    turn.TerminationCode = cancellationWasRequested ? "cancelled" : request.TerminationCode;
                    turn.TerminationDetail = BoundTerminationDetail(
                        cancellationWasRequested
                            ? "Stream was cancelled by user"
                            : request.TerminationDetail);
                    if (request.ExecutionId.HasValue)
                    {
                        turn.ExecutionId = request.ExecutionId;
                    }
                }

                if (request.Output != null)
                {
                    turn.ChatRunOutputJson = JsonSerializer.Serialize(request.Output, JsonOptions);
                    turn.UsageJson = request.Output.Usage != null
                        ? JsonSerializer.Serialize(request.Output.Usage, JsonOptions)
                        : turn.UsageJson;

                    if (request.Output.NewFiles is { Count: > 0 })
                    {
                        turn.FilesCreated = JsonSerializer.Serialize(request.Output.NewFiles, JsonOptions);
                    }

                    if (request.Output.ModifiedFiles is { Count: > 0 })
                    {
                        turn.FilesModified = JsonSerializer.Serialize(request.Output.ModifiedFiles, JsonOptions);
                    }
                }

                turn.LastUpdated = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to terminalize turn {TurnId} for conversation {ConversationId}",
                    request.TurnId,
                    request.ConversationId);
                throw;
            }
        }

        return await ExecuteAtomicFencedWriteAsync<bool>(
            db,
            request.TurnId,
            request.ExecutionId,
            TerminalizeAsync,
            ct);
    }

    public async Task<bool> CheckpointTurnAsync(
        Guid turnId,
        Guid messageId,
        string content,
        string? thinkingBlocksJson,
        int checkpointVersion,
        CancellationToken ct = default,
        Guid? expectedExecutionId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;

        if (string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            var turn = await db.ConversationTurns.FirstOrDefaultAsync(t => t.Id == turnId, ct);
            if (turn == null
                || IsTerminalTurnStatus(turn.Status)
                || turn.CheckpointVersion >= checkpointVersion
                || (expectedExecutionId.HasValue && turn.ExecutionId != expectedExecutionId))
            {
                return false;
            }

            turn.CheckpointVersion = checkpointVersion;
            turn.LastUpdated = now;

            var message = await db.NotebookConversationMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (message == null)
            {
                return false;
            }

            message.Content = content;
            if (thinkingBlocksJson != null)
            {
                message.ThinkingBlocksJson = thinkingBlocksJson;
            }

            await db.SaveChangesAsync(ct);
            return true;
        }

        // Hot-path streaming checkpoints use conditional ExecuteUpdate for the turn fence and a
        // separate message write. Explicit Serializable transactions belong on fence/cancel/
        // terminalize writes only; wrapping every token checkpoint in BeginTransactionAsync with
        // MARS enabled spams SavepointsDisabledBecauseOfMARS and adds lock churn.
        var turnQuery = db.ConversationTurns.Where(t =>
            t.Id == turnId
            && (t.Status == "streaming" || t.Status == "pending_client_tool")
            && t.CheckpointVersion < checkpointVersion);

        if (expectedExecutionId.HasValue)
        {
            turnQuery = turnQuery.Where(t => t.ExecutionId == expectedExecutionId);
        }

        var updated = await turnQuery.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(t => t.CheckpointVersion, checkpointVersion)
                .SetProperty(t => t.LastUpdated, now),
            ct);

        if (updated == 0)
        {
            return false;
        }

        var messageStub = new NotebookConversationMessage { Id = messageId };
        db.Attach(messageStub);
        messageStub.Content = content;
        db.Entry(messageStub).Property(x => x.Content).IsModified = true;

        if (thinkingBlocksJson != null)
        {
            messageStub.ThinkingBlocksJson = thinkingBlocksJson;
            db.Entry(messageStub).Property(x => x.ThinkingBlocksJson).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> PruneIncompleteToolCallsInContextAsync(
        ApplicationDbContext db,
        Guid conversationId,
        int turnIndex,
        CancellationToken ct = default)
    {
        var turnMessages = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turnIndex)
            .ToListAsync(ct);

        var toolResultIds = new HashSet<string>(turnMessages
            .Where(m => m.Role == DataModelChatRole.Tool && !string.IsNullOrWhiteSpace(m.ToolCallId))
            .Select(m => m.ToolCallId!)
            .Where(id => !string.IsNullOrWhiteSpace(id)));

        var anyChanges = false;

        foreach (var msg in turnMessages)
        {
            if (msg.Role != DataModelChatRole.Assistant || string.IsNullOrWhiteSpace(msg.ToolCalls))
            {
                continue;
            }

            try
            {
                var calls = JsonSerializer.Deserialize<List<ChatToolCall>>(msg.ToolCalls!, JsonOptions)
                    ?? new List<ChatToolCall>();
                var pruned = calls.Where(tc => !string.IsNullOrWhiteSpace(tc.Id) && toolResultIds.Contains(tc.Id!)).ToList();

                if (pruned.Count == calls.Count)
                {
                    continue;
                }

                msg.ToolCalls = pruned.Count > 0
                    ? JsonSerializer.Serialize(pruned, JsonOptions)
                    : null;
                db.Entry(msg).Property(x => x.ToolCalls).IsModified = true;
                anyChanges = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to prune tool calls for message {MessageId} in conversation {ConversationId} turn {TurnIndex}",
                    msg.Id,
                    conversationId,
                    turnIndex);
            }
        }

        return anyChanges;
    }
}
