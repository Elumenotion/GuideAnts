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

    public async Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title)
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
