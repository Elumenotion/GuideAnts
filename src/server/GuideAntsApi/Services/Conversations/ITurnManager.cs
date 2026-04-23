using AntRunner.Chat;
using GuideAntsApi.Models;

namespace GuideAntsApi.Services.Conversations;

public interface ITurnManager
{
    Task<Turn> CreateTurnAsync(Guid conversationId, string assistantName, string instructions);
    Task CompleteTurnAsync(Guid conversationId, Turn turn, ChatRunOutput output);
    Task<List<Turn>> GetTurnsAsync(Guid conversationId);
    Task<List<NotebookFileDto>> GetFilesCreatedInTurnAsync(Guid conversationId, int turnIndex);
    Task<List<NotebookFileDto>> GetFilesCreatedInConversationAsync(Guid conversationId);
    Task TrackFilesForTurnAsync(Guid conversationId, int turnIndex, List<string> createdFiles, List<string> modifiedFiles);
    Task<List<FileLineageEventDto>> GetFileOperationsForTurnAsync(Guid conversationId, int turnIndex);
} 