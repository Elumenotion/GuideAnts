using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations.Commands;

public class ConversationCommandService : IConversationCommandService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationCommandService> _logger;

    public ConversationCommandService(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationCommandService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title)
        => CreateConversationAsync(notebookId, title, null);

    public async Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title, Guid? sourceConversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebook = await db.Notebooks.FirstOrDefaultAsync(n => n.Id == notebookId);
        if (notebook == null) throw new KeyNotFoundException("Notebook not found");

        var conv = new NotebookConversation
        {
            NotebookId = notebookId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim()
        };
        db.NotebookConversations.Add(conv);

        if (sourceConversationId.HasValue)
        {
            var sourceId = sourceConversationId.Value;
            var sourceExists = await db.NotebookConversations
                .AnyAsync(c => c.Id == sourceId && c.NotebookId == notebookId);
            if (!sourceExists)
                throw new KeyNotFoundException($"Source conversation {sourceConversationId} not found in notebook {notebookId}");

            // Read ONLY the columns the clone needs, as three flat projections.
            //
            // This replaces an entity-graph load (Messages + TurnsWithIncludes).
            // Messages and Turns are two top-level collections, so EF rendered
            // that as a cartesian join: every message row repeated once per turn,
            // each copy carrying every LOB column (Content/ToolCalls/
            // ThinkingBlocksJson plus the turn's Instructions/UsageJson/
            // ChatRunOutputJson). For a 377-message, 12-turn conversation that is
            // 4,524 result rows and ~2.6 GB streamed to the server (measured on
            // the live stack), which is why one Copy took ~70-120 s. The
            // projections below read only the ~1 MB the copy actually inserts.

            var sourceMessages = await db.NotebookConversationMessages
                .AsNoTracking()
                .Where(m => m.NotebookConversationId == sourceId)
                .OrderBy(m => m.TurnIndex)
                .ThenBy(m => m.MessageSequence)
                .Select(m => new
                {
                    m.Id,
                    m.UserId,
                    m.ExternalUserIdentity,
                    m.Role,
                    m.AssistantId,
                    m.Content,
                    m.AssistantName,
                    m.ModelDeploymentId,
                    m.ThinkingBlocksJson,
                    m.ToolCalls,
                    m.FunctionName,
                    m.ToolCallId,
                    m.Created,
                    m.TurnIndex,
                    m.MessageSequence,
                    m.MessageContentType,
                    m.IsEdited,
                    m.LastEditedByUserId,
                    m.LastEditedAt,
                    m.IsStreaming
                })
                .ToListAsync();

            // Edit history and attachments are read keyed by the source message id
            // (indexed), so there is no join fan-out in either direction.
            var editHistoryBySourceMessage = (await db.MessageEditHistories
                .AsNoTracking()
                .Where(h => db.NotebookConversationMessages
                    .Any(m => m.Id == h.MessageId && m.NotebookConversationId == sourceId))
                .Select(h => new
                {
                    h.MessageId,
                    h.OriginalContent,
                    h.OriginalToolCalls,
                    h.FirstEditedByUserId,
                    h.FirstEditedAt
                })
                .ToListAsync())
                .ToDictionary(h => h.MessageId);

            var attachmentsBySourceMessage = (await db.MessageAttachments
                .AsNoTracking()
                .Where(a => db.NotebookConversationMessages
                    .Any(m => m.Id == a.MessageId && m.NotebookConversationId == sourceId))
                .OrderBy(a => a.MessageId)
                .ThenBy(a => a.OrderIndex)
                .Select(a => new
                {
                    a.MessageId,
                    a.NotebookFileId,
                    a.RelativePath,
                    a.UploadType,
                    a.Type,
                    a.OrderIndex,
                    a.Created
                })
                .ToListAsync())
                .GroupBy(a => a.MessageId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Clone messages with fresh primary keys, preserving ordering (TurnIndex/MessageSequence),
            // timestamps, role, tool-call state, attribution, edit history and attachments.
            foreach (var m in sourceMessages)
            {
                var clone = new NotebookConversationMessage
                {
                    NotebookConversationId = conv.Id,
                    UserId = m.UserId,
                    ExternalUserIdentity = m.ExternalUserIdentity,
                    Role = m.Role,
                    AssistantId = m.AssistantId,
                    Content = m.Content,
                    AssistantName = m.AssistantName,
                    ModelDeploymentId = m.ModelDeploymentId,
                    ThinkingBlocksJson = m.ThinkingBlocksJson,
                    ToolCalls = m.ToolCalls,
                    FunctionName = m.FunctionName,
                    ToolCallId = m.ToolCallId,
                    Created = m.Created,
                    TurnIndex = m.TurnIndex,
                    MessageSequence = m.MessageSequence,
                    MessageContentType = m.MessageContentType,
                    IsEdited = m.IsEdited,
                    LastEditedByUserId = m.LastEditedByUserId,
                    LastEditedAt = m.LastEditedAt,
                    IsStreaming = m.IsStreaming
                };

                if (editHistoryBySourceMessage.TryGetValue(m.Id, out var history))
                {
                    clone.EditHistory = new MessageEditHistory
                    {
                        MessageId = clone.Id,
                        OriginalContent = history.OriginalContent,
                        OriginalToolCalls = history.OriginalToolCalls,
                        FirstEditedByUserId = history.FirstEditedByUserId,
                        FirstEditedAt = history.FirstEditedAt
                    };
                }

                // Attachments point at the same notebook file / relative path (same notebook), so
                // they are safe to re-reference rather than duplicate.
                clone.Attachments = attachmentsBySourceMessage.TryGetValue(m.Id, out var sourceAttachments)
                    ? sourceAttachments.Select(a => new MessageAttachment
                    {
                        MessageId = clone.Id,
                        NotebookFileId = a.NotebookFileId,
                        RelativePath = a.RelativePath,
                        UploadType = a.UploadType,
                        Type = a.Type,
                        OrderIndex = a.OrderIndex,
                        Created = a.Created
                    }).ToList()
                    : new List<MessageAttachment>();

                conv.Messages.Add(clone);
            }

            // Clone turn rows for structure (assistant attribution, instructions, file lists, status).
            // Per-turn usage (UsageJson) and raw run output (ChatRunOutputJson) are deliberately NOT
            // copied: usage metrics would be double-counted and the run output is not part of the
            // conversation transcript.
            var sourceTurns = await db.ConversationTurns
                .AsNoTracking()
                .Where(t => t.NotebookConversationId == sourceId)
                .OrderBy(t => t.TurnIndex)
                .Select(t => new
                {
                    t.TurnIndex,
                    t.AssistantName,
                    t.ModelDeploymentId,
                    t.Instructions,
                    t.FilesCreated,
                    t.FilesModified,
                    t.WorkingDirectory,
                    t.Created,
                    t.Status,
                    t.TerminalizedAt,
                    t.TerminationCode,
                    t.TerminationDetail,
                    t.CheckpointVersion
                })
                .ToListAsync();

            foreach (var t in sourceTurns)
            {
                conv.Turns.Add(new ConversationTurn
                {
                    NotebookConversationId = conv.Id,
                    TurnIndex = t.TurnIndex,
                    AssistantName = t.AssistantName,
                    ModelDeploymentId = t.ModelDeploymentId,
                    Instructions = t.Instructions,
                    FilesCreated = t.FilesCreated,
                    FilesModified = t.FilesModified,
                    WorkingDirectory = t.WorkingDirectory,
                    Created = t.Created,
                    LastUpdated = t.Created,
                    Status = t.Status,
                    TerminalizedAt = t.TerminalizedAt,
                    TerminationCode = t.TerminationCode,
                    TerminationDetail = t.TerminationDetail,
                    CheckpointVersion = t.CheckpointVersion
                });
            }
        }

        await db.SaveChangesAsync();
        return new NotebookConversationListDto(conv.Id, conv.Title, conv.Created, conv.Created);
    }

    public async Task RenameConversationAsync(Guid conversationId, string title)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new KeyNotFoundException();

        conv.Title = string.IsNullOrWhiteSpace(title) ? conv.Title : title.Trim();
        await db.SaveChangesAsync();
    }

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conv == null) return;

        _logger.LogCritical("🚨 DELETING CONVERSATION {ConversationId} - THIS WILL CASCADE DELETE ALL MESSAGES!", conversationId);
        db.NotebookConversations.Remove(conv);

        try
        {
            await db.SaveChangesAsync();
            _logger.LogCritical("🚨 CONVERSATION {ConversationId} DELETED - ALL MESSAGES GONE!", conversationId);
        }
        catch (DbUpdateConcurrencyException)
        {
            var stillExists = await db.NotebookConversations
                .AnyAsync(c => c.Id == conversationId);

            if (!stillExists)
            {
                _logger.LogInformation("Conversation {ConversationId} was already deleted by another request - treating as success", conversationId);
                return;
            }

            throw;
        }
    }

    public async Task EditMessageAsync(Guid messageId, string newContent)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var currentUserService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        var currentUser = await currentUserService.GetCurrentUserAsync().ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Authenticated user is required.");
        var currentUserId = currentUser.UserId;

        var message = await db.NotebookConversationMessages
            .Include(m => m.NotebookConversation)
                .ThenInclude(nc => nc.Notebook)
            .Include(m => m.EditHistory)
            .FirstOrDefaultAsync(m => m.Id == messageId)
            ?? throw new KeyNotFoundException("Message not found");

        if (message.Role != ChatRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be edited");
        }

        if (!message.IsEdited && message.EditHistory == null)
        {
            var editHistory = new MessageEditHistory
            {
                MessageId = messageId,
                OriginalContent = message.Content,
                OriginalToolCalls = message.ToolCalls,
                FirstEditedByUserId = currentUserId,
                FirstEditedAt = DateTime.UtcNow
            };
            db.MessageEditHistories.Add(editHistory);
        }

        message.Content = newContent;
        message.IsEdited = true;
        message.LastEditedByUserId = currentUserId;
        message.LastEditedAt = DateTime.UtcNow;
        message.ToolCalls = null;

        await db.SaveChangesAsync();
    }
}
