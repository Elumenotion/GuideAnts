using AntRunner.Chat.Abstractions;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Conversations;

public interface IMessageManager
{
    Task AddMessageAsync(Guid conversationId, ChatMessage message, int turnIndex, string? assistantName = null, string? modelDeploymentId = null, int? messageSequence = null);
    Task<List<ChatMessage>> GetMessagesAsync(Guid conversationId, string assistantName);
    Task RemoveMessagesFromAsync(Guid conversationId, DateTime fromTimestamp);
    
    // Integration with existing file architecture
    Task<List<NotebookFileDto>> GetFilesReferencedInConversationAsync(Guid conversationId);
} 