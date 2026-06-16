using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Commands;

public interface IConversationCommandService
{
    Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title);
    Task RenameConversationAsync(Guid conversationId, string title);
    Task DeleteConversationAsync(Guid conversationId);
    Task EditMessageAsync(Guid messageId, string newContent);
}
