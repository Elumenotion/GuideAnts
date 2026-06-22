using GuideAntsApi.Models;

namespace GuideAntsApi.Models.Guides;

// Summary view for guide list
public record GuideDto(
    Guid Id,
    string Name,
    string Description,
    string? AvatarUrl,
    string? ModelId,
    string? ModelName,
    int ToolCount,
    int CrewMemberCount,
    DateTime Created,
    DateTime? Updated
);

// Full guide details including all relations
public record GuideDetailsDto(
    GuideDto Guide,
    string? Instructions,
    string? HomePageMarkdown,
    float? Temperature,
    double? TopP,
    string? ReasoningEffort,
    List<ToolAssignmentDto> Tools,
    List<ContextOptionDto> ContextOptions,
    List<AuthProviderDto>? AuthProviders,
    List<CustomToolDto> CustomTools,
    List<FileDto> Files,
    List<ConversationStarterDto> ConversationStarters,
    List<CrewSummaryDto> Crews
) { public List<EnvironmentVariableDto>? EnvironmentVariables { get; init; } }

// Create guide request
public record CreateGuideDto(
    string Name,
    string Description,
    string? Instructions,
    string? HomePageMarkdown,
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
    List<AuthProviderDto>? AuthProviders,
    List<FileUploadDto>? Files,
    List<string>? ConversationStarters,
    List<Guid>? CrewMemberIds  // Assistant IDs to include in crew
) {
    public Guid? ProjectId { get; init; }
    public List<EnvironmentVariableDto>? EnvironmentVariables { get; init; }
}

// Update guide request
public record UpdateGuideDto(
    string Name,
    string Description,
    string? Instructions,
    string? HomePageMarkdown,
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
    List<AuthProviderDto>? AuthProviders,
    List<Guid>? FileIdsToKeep,  // IDs of existing files to keep (omitted files are deleted)
    List<FileUploadDto>? FilesToAdd,  // New files to upload
    List<string>? ConversationStarters,
    List<Guid>? CrewMemberIds  // Assistant IDs to include in crew
) {
    public Guid? ProjectId { get; init; }
    public List<EnvironmentVariableDto>? EnvironmentVariables { get; init; }
}

// Supporting DTOs
public record ToolAssignmentDto(
    Guid ToolId,
    string ToolType,
    string DisplayName,
    bool IsCustom
);

public record CustomToolDto(
    string Name,
    string OpenApiSpec,
    string? ApiHost,
    OpenApiAuthConfigDto? AuthConfig,
    List<OpenApiOperationDto>? Operations = null
);

public record OpenApiAuthConfigDto(
    string AuthType, // "oauth", "service_http", "api_key"
    string? ClientId,
    string? Tenant,
    List<string>? Scopes,
    string? ValueTemplate,
    string? HeaderName,
    string? UserConfigPolicy // "required", "optional", "none"
);

public record OpenApiOperationDto(
    Guid Id,
    string OperationId,
    string Method,
    string Path,
    string? Summary,
    string? SchemaFragment = null,
    string? ToolDefinition = null
);

public record UpdateOperationDto(
    string SchemaFragmentJson
);

public record PreviewToolDefinitionDto(
    string SchemaFragmentJson,
    string OpenApiSpecJson
);

public record ContextOptionDto(
    string Key,
    string? Value
);

public record AuthProviderDto(
    Guid? Id,
    string ProviderId,
    string AuthType,
    string? ClientId,
    string? Tenant,
    string? HeaderName,
    string? ValueTemplate,
    string? UserConfigPolicy,
    List<string>? Scopes
);

public record FileDto(
    Guid Id,
    string FolderKind,
    string? VectorStoreName,
    string RelativePath,
    string? ContentType,
    DateTime Created,
    AssistantFileMarkdownShadowDto? MarkdownShadow
);

public record FileUploadDto(
    string FolderKind,
    string? VectorStoreName,
    string RelativePath,
    byte[] ContentBytes,
    string? ContentType
);

public record ConversationStarterDto(
    Guid? Id,
    string Prompt,
    int OrderIndex
);

public record CrewSummaryDto(
    Guid CrewId,
    string Name,
    List<CrewMemberDto> Members
);

public record CrewMemberDto(
    Guid AssistantId,
    string Name,
    string? AvatarUrl,
    bool IsGlobal,
    int DisplayOrder
);

public record GuideRuntimeValidationRequest(
    List<GuideRuntimeValidationMember> Members
);

public record GuideRuntimeValidationMember(
    string Role, // "guide" or "assistant"
    Guid? EntityId,
    string DisplayName,
    string? ModelId
);

public record GuideRuntimeValidationDto(
    bool IsValid,
    List<string> Profiles,
    List<string> Conflicts,
    List<string> Warnings
);
