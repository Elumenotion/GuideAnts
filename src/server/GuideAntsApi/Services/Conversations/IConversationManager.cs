namespace GuideAntsApi.Services.Conversations;

public interface IConversationManager
{
    Task<AntRunner.Chat.Conversation> CreateConversationAsync(Guid conversationId, string assistantName);
    Task<AntRunner.Chat.Conversation> LoadConversationAsync(Guid conversationId);
    Task SaveConversationAsync(AntRunner.Chat.Conversation conversation);
    Task<AntRunner.Chat.Conversation> RestoreConversationStateAsync(Guid conversationId);
    Task<ConversationStateDto> GetCurrentStateAsync(Guid conversationId);
    Task<string> GetCurrentAssistantAsync(Guid conversationId);
    Task<string> GetCurrentModelAsync(Guid conversationId);
    void InvalidateCache(Guid conversationId);
}

public class ConversationStateDto
{
    public Guid ConversationId { get; set; }
    public string? CurrentAssistantName { get; set; }
    public string? CurrentModelDeploymentId { get; set; }
    public string? LastInstructions { get; set; }
    public DateTime? LastActivity { get; set; }
    public int CurrentTurnIndex { get; set; }
    public List<string>? LastTurnFilesCreated { get; set; }
    public List<string>? LastTurnFilesModified { get; set; }
} 