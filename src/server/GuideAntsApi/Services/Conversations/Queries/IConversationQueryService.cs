using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Queries;

public interface IConversationQueryService
{
    Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId);
    Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId);
    Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId);
    Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query);
}
