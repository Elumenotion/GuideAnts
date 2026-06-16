using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
            Status = request.InitialStatus ?? "completed"
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
            LastUpdated = DateTime.UtcNow
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
        turn.LastUpdated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Guid> StartAssistantMessageAsync(StartAssistantMessageRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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
        TouchTurn(db, request.TurnId);
        await db.SaveChangesAsync(ct);
        return msg.Id;
    }

    public async Task AppendOrFinalizeAssistantMessageAsync(AssistantMessageUpdateRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

        TouchTurn(db, request.TurnId);
        await db.SaveChangesAsync(ct);
    }

    public async Task FinalizeStreamingAssistantMessageIfStillStreamingAsync(
        Guid messageId,
        Guid turnId,
        string content,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

        TouchTurn(db, turnId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> CreateToolMessageAsync(CreateToolMessageRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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
        TouchTurn(db, request.TurnId);
        await db.SaveChangesAsync(ct);
        return toolMessage.Id;
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
                var calls = JsonSerializer.Deserialize<List<ChatToolCall>>(msg.ToolCalls!, JsonOptions) ?? new List<ChatToolCall>();
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

        if (anyChanges)
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

    private static void TouchTurn(ApplicationDbContext db, Guid turnId)
    {
        var turn = db.ConversationTurns.First(t => t.Id == turnId);
        turn.LastUpdated = DateTime.UtcNow;
    }
}
