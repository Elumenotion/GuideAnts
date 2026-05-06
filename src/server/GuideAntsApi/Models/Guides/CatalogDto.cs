using System.Text.Json.Nodes;

namespace GuideAntsApi.Models.Guides;

// Runtime config descriptor for all model providers
public record ModelRuntimeConfigDto(
    string RouterModelId,
    string RuntimeProfileId,
    JsonObject? LoadParams
);

public record SamplingParameterPolicyDto(
    string Key,
    string Label,
    string Description,
    double Min,
    double Max,
    double Step,
    double RecommendedDefault,
    int DisplayOrder
);

// Model catalog entry
public record ModelDto(
    string ModelId,
    string DisplayName,
    string? Description,
    string? ReasoningChoicesJson,
    bool IsActive,
    int? DisplayOrder,
    ModelRuntimeConfigDto? RuntimeConfig,
    IReadOnlyList<SamplingParameterPolicyDto>? SamplingParameterPolicy,
    IReadOnlyList<string>? ReasoningChoices,
    string? DefaultReasoningChoice
);

// Tool catalog entry
public record ToolDto(
    Guid Id,
    string ToolType,
    string DisplayName,
    string? Description,
    string? Category,
    bool IsActive,
    int? DisplayOrder
);

// Global assistant template
public record GlobalAssistantDto(
    Guid Id,
    string Name,
    string Description,
    string? DefaultInstructions,
    string? DefaultModelId,
    string? AvatarUrl,
    bool IsActive,
    int? DisplayOrder,
    List<Guid> DefaultToolIds
);

// Global assistant with full details
public record GlobalAssistantDetailsDto(
    GlobalAssistantDto Assistant,
    List<ToolDto> Tools
);
