namespace GuideAntsApi.Models.Conversations;

using GuideAntsApi.DataModel.Models;
using AntRunner.Chat;
using System.Collections.Generic;

public record MessageDto(
    Guid Id,
    ChatRole Role,
    string Content,
    Guid? UserId,
    string? AssistantName,
    bool IsEdited,
    DateTime? LastEditedAt,
    DateTime Created,
    string? OriginalContent,
    IReadOnlyList<ToolCallDto>? ToolCalls = null,
    string? ToolCallId = null,
    string? FunctionName = null,
    IReadOnlyList<AttachedFileDto>? Attachments = null,
    MessageContentType MessageContentType = MessageContentType.Text,
    Guid? AttachedNotebookFileId = null, // DEPRECATED: kept for backward compatibility
    int? TurnIndex = null,
    IReadOnlyList<string>? TurnFilesCreated = null,
    IReadOnlyList<string>? TurnFilesModified = null,
    string? UserName = null,
    string? UserEmail = null
);

public record AttachedFileDto(
    Guid? NotebookFileId,
    string? RelativePath,
    ContentUploadType? UploadType,
    string FileName,
    string FileType, // 'image', 'audio', 'text', 'folder', 'other'
    long FileSize,
    string? PreviewUrl,
    AttachmentType Type
);

public record ToolCallDto(
    string Id,
    string Type,
    ToolCallFunctionDto Function
);

public record ToolCallFunctionDto(
    string Name,
    string Arguments
);

public record ConversationDto(Guid NotebookId, DateTime Created, IReadOnlyList<MessageDto> Messages);

public record ConversationTurnStatusDto(
    Guid TurnId,
    int TurnIndex,
    string Status,
    string? TerminationCode,
    DateTime? TerminalizedAt);

public record ConversationLockStatusDto(
    string LockedByUserName,
    DateTime LockedAt);

public record ConversationStreamingPreviewDto(
    Guid MessageId,
    string Content,
    string? ToolCallsJson,
    int TurnIndex);

public record NotebookConversationWithMessagesDto(
    Guid Id,
    string Title,
    string? AssistantName,
    DateTime Created,
    DateTime? LastActivity,
    IReadOnlyList<MessageDto> Messages,
    ConversationTurnStatusDto? ActiveTurn = null,
    ConversationLockStatusDto? Lock = null,
    ConversationStreamingPreviewDto? StreamingPreview = null);

public class SendMessageRequest
{
    public string Instructions { get; set; } = string.Empty;
    public string? AssistantName { get; set; }
    public string? ModelDeploymentId { get; set; }
    public bool? UseAssistantDefinitionModel { get; set; }

    // NEW: Optional list of notebook file attachments to include with this message
    public List<AttachmentDto>? Attachments { get; set; }

    // NEW: Optional external OAuth tokens for accessing external APIs
    public Dictionary<string, string>? ExternalAuthTokens { get; set; }

    // NEW: Optional client-provided context message (from page initialization callback)
    public string? ClientContext { get; set; }

    // Optional client-provided message list to prepend to the run prompt.
    public List<AntRunner.Chat.Abstractions.ChatMessage>? ClientMessages { get; set; }

    // Optional client-exposed tools to advertise for this run only.
    public List<AntRunner.Chat.Abstractions.ChatToolDefinition>? ClientToolDefinitions { get; set; }
}

public class EditMessageRequest
{
    public string Content { get; set; } = string.Empty;
}

public record SendMessageResponse(ConversationDto Conversation, UsageResponse Usage);

public class UndoMessageRequest
{
    public Guid MessageId { get; set; }
}

public record AttachmentDto(
    Guid? NotebookFileId,
    ContentUploadType UploadType,
    string? RelativePath = null
);
