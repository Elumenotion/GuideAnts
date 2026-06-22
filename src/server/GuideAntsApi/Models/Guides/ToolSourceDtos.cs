namespace GuideAntsApi.Models.Guides;

public record ToolSourceValidationMessageDto(
    string Code,
    string Message,
    string? Field,
    string Severity
);

public record ToolDefinitionPreviewResultDto(
    string SourceKind,
    string ActionType,
    string ToolDefinition,
    List<string> HiddenParameters,
    Dictionary<string, object>? ResponseSchemas,
    List<ToolSourceValidationMessageDto> ValidationMessages
);
