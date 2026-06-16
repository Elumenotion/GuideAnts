using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Conversations.Mapping;

public interface IConversationHistoryBuilder
{
    bool IsNewConversation(NotebookConversation conv);
    bool IsAssistantSwitch(NotebookConversation conv, string assistantName);
    string HandoffSystemMessage { get; }

    IReadOnlyList<NotebookConversationMessage> FilterMessages(
        NotebookConversation conv,
        string assistantName,
        bool isAssistantSwitch);

    Task<List<ChatMessage>> PrepareMessagesForAssistantAsync(
        NotebookConversation conv,
        string assistantName,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(
        NotebookConversation conv,
        string newAssistantName,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> BuildOpenAiMessagesAsync(
        NotebookConversation conv,
        string assistantName,
        CancellationToken cancellationToken = default);

    Task<List<ChatMessage>> BuildPublishedMessagesForAssistantAsync(
        NotebookConversation conv,
        string assistantName,
        string? clientContext,
        CancellationToken cancellationToken = default);
}
