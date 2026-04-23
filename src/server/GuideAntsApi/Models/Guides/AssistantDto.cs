namespace GuideAntsApi.Models.Guides;

// Summary view for assistant list
public record AssistantDto(
    Guid Id,
    string Name,
    string Description,
    string? AvatarUrl,
    string? ModelId,
    string? ModelName,
    int ToolCount,
    List<string> CrewNames,
    DateTime Created,
    DateTime? Updated
);

// Full assistant details including all relations
public record AssistantDetailsDto(
    AssistantDto Assistant,
    string? Instructions,
    float? Temperature,
    double? TopP,
    string? ReasoningEffort,
    List<ToolAssignmentDto> Tools,
    List<ContextOptionDto> ContextOptions,
    List<CustomToolDto> CustomTools,
    List<FileDto> Files,
    List<ConversationStarterDto> ConversationStarters
);

// Create assistant request
public record CreateAssistantDto(
    string Name,
    string Description,
    string? Instructions,
    string? ModelId,
    float? Temperature,
    double? TopP,
    string? ReasoningEffort,
    string? SamplingParametersJson,
    byte[]? AvatarImageBytes,
    string? AvatarContentType,
    List<Guid>? ToolIds,
    List<CustomToolDto>? CustomTools,
    List<ContextOptionDto>? ContextOptions,
    List<FileUploadDto>? Files,
    List<string>? ConversationStarters
);

// Update assistant request
public record UpdateAssistantDto(
    string Name,
    string Description,
    string? Instructions,
    string? ModelId,
    float? Temperature,
    double? TopP,
    string? ReasoningEffort,
    string? SamplingParametersJson,
    byte[]? AvatarImageBytes,
    string? AvatarContentType,
    List<Guid>? ToolIds,
    List<CustomToolDto>? CustomTools,
    List<ContextOptionDto>? ContextOptions,
    List<Guid>? FileIdsToKeep,  // IDs of existing files to keep (omitted files are deleted)
    List<FileUploadDto>? FilesToAdd,  // New files to upload
    List<string>? ConversationStarters
);
