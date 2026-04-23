using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations;

public interface IConversationService
{
    // Conversation-specific methods (KEEP - these are the correct ones)
    Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId);
    Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId);
    Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId);
    Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title);
    Task RenameConversationAsync(Guid conversationId, string title);
    Task DeleteConversationAsync(Guid conversationId);
    
    IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken = default);
    
    Task EditMessageAsync(Guid messageId, string newContent);
    Task UndoLastForConversationAsync(Guid conversationId);
    Task UndoForConversationAsync(Guid conversationId, Guid messageId);
    
    // User conversations across all projects
    Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query);
}