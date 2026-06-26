using GuideAntsApi.Models.Conversations;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Services.Conversations;

public interface IPublishedConversationService
{
	/// <summary>
	/// Streams an assistant response for a published conversation without any authenticated principal.
	/// </summary>
	/// <param name="conversationId">Target conversation</param>
	/// <param name="request">Send message request</param>
    /// <param name="publisherId">Optional opaque publisher correlation id</param>
    /// <param name="externalUserIdentity">External user identity from auth validation</param>
    /// <param name="internalUserId">Internal Users.Id for AppIdentity-mode published guides</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming events compatible with main client SSE event types</returns>
	IAsyncEnumerable<StreamingEvent> SendMessageStreamAsync(
        Guid conversationId,
        SendMessageRequest request,
        string? publisherId,
        string? externalUserIdentity,
        Guid? internalUserId = null,
        CancellationToken cancellationToken = default);

	/// <summary>
	/// Resumes a published conversation after external (client-handled) tool results are posted.
	/// Executes any deferred server-handled tools for the last assistant turn, then continues the LLM run.
	/// </summary>
	/// <param name="conversationId">Target conversation</param>
	/// <param name="publisherId">Optional opaque publisher correlation id</param>
	/// <param name="externalUserIdentity">External user identity from auth validation</param>
	/// <param name="internalUserId">Internal Users.Id for AppIdentity-mode published guides</param>
	/// <param name="clientToolDefinitions">Optional run-scoped client tool definitions to expose during resume.</param>
	/// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Streaming events compatible with main client SSE event types</returns>
    IAsyncEnumerable<StreamingEvent> ResumeAfterExternalToolResultsStreamAsync(
        Guid conversationId,
        string? publisherId,
        string? externalUserIdentity,
        Guid? internalUserId = null,
        IReadOnlyList<ChatToolDefinition>? clientToolDefinitions = null,
        CancellationToken cancellationToken = default);

    Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title);
    Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId);
    Task UndoLastForConversationAsync(Guid conversationId);
}


