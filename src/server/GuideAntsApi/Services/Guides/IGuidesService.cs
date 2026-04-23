using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides;

public interface IGuidesService
{
    // Guides (TeamAssistant where Kind=Guide)
    Task<IEnumerable<GuideDto>> GetGuidesAsync();
    Task<GuideDetailsDto?> GetGuideAsync(Guid guideId);
    Task<GuideDto> CreateGuideAsync(CreateGuideDto dto);
    Task<GuideDto> UpdateGuideAsync(Guid guideId, UpdateGuideDto dto);
    Task<bool> DeleteGuideAsync(Guid guideId);
    Task<GuideDto> DuplicateGuideAsync(Guid guideId);
    Task<byte[]?> GetGuideAvatarBytesAsync(Guid guideId);
    
    // Assistants (TeamAssistant where Kind=Assistant)
    Task<IEnumerable<AssistantDto>> GetAssistantsAsync();
    Task<AssistantDetailsDto?> GetAssistantAsync(Guid assistantId);
    Task<AssistantDto> CreateAssistantAsync(CreateAssistantDto dto);
    Task<AssistantDto> UpdateAssistantAsync(Guid assistantId, UpdateAssistantDto dto);
    Task<bool> DeleteAssistantAsync(Guid assistantId);
    Task<AssistantDto> DuplicateAssistantAsync(Guid assistantId);
    Task<byte[]?> GetAssistantAvatarBytesAsync(Guid assistantId);

    // OpenAPI Operations
    Task<OpenApiOperationDto?> GetOperationAsync(Guid operationId);
    Task<OpenApiOperationDto> UpdateOperationAsync(Guid operationId, UpdateOperationDto dto);
    Task<string> PreviewToolDefinitionAsync(PreviewToolDefinitionDto dto);

    // Validation
    Task<GuideRuntimeValidationDto> ValidateRuntimeCompatibilityAsync(GuideRuntimeValidationRequest request);
}
