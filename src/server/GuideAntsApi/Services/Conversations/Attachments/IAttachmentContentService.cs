using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Attachments;

public interface IAttachmentContentService
{
    Task AddAttachmentsToUserMessageAsync(
        Guid userMessageId,
        Guid notebookId,
        IReadOnlyList<AttachmentDto> attachments,
        CancellationToken cancellationToken = default);

    Task AddAttachmentsToUserMessageAsync(
        ApplicationDbContext db,
        Guid userMessageId,
        Guid notebookId,
        IReadOnlyList<AttachmentDto> attachments,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> CreateOpenAiMessagesFromNotebookFileAsync(
        Guid notebookFileId,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> CreateOpenAiMessagesFromNotebookFileAsync(
        ApplicationDbContext db,
        Guid notebookFileId,
        CancellationToken cancellationToken = default);

    Task<List<ChatContent>> CreateOpenAiContentFromNotebookFileAsync(
        Guid notebookFileId,
        CancellationToken cancellationToken = default);

    Task<List<ChatContent>> CreateOpenAiContentFromNotebookFileAsync(
        ApplicationDbContext db,
        Guid notebookFileId,
        CancellationToken cancellationToken = default);

    Task<List<ChatContent>> ExpandAttachmentToChatContentsAsync(
        MessageAttachment attachment,
        CancellationToken cancellationToken = default);

    Task<List<ChatContent>> ExpandAttachmentToChatContentsAsync(
        ApplicationDbContext db,
        MessageAttachment attachment,
        CancellationToken cancellationToken = default);

    Task<List<ChatContent>> CreateOpenAiContentFromLoadedFileAsync(
        NotebookFile notebookFile,
        CancellationToken cancellationToken = default);
}
