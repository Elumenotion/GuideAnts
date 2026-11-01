using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Commands;

public interface IConversationCommandService
{
    Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title);

    /// <summary>
    /// Creates a new conversation in <paramref name="notebookId"/>. When
    /// <paramref name="sourceConversationId"/> is provided, the new conversation is a copy:
    /// all messages and turn rows of the source conversation are cloned (with fresh primary keys),
    /// preserving ordering (TurnIndex/MessageSequence), timestamps, tool-call state, attachments,
    /// and edit history. Per-turn usage and raw ChatRunOutput are NOT copied, so usage metrics are
    /// not double-counted. Returns 404-equivalent KeyNotFoundException when the source is not found
    /// in the notebook.
    /// </summary>
    Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title, Guid? sourceConversationId = null);
    Task RenameConversationAsync(Guid conversationId, string title);
    Task DeleteConversationAsync(Guid conversationId);
    Task EditMessageAsync(Guid messageId, string newContent);
}
