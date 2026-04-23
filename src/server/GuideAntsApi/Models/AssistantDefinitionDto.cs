namespace GuideAntsApi.Models;
 
public record AssistantDefinitionDto(
    Guid Id,
    string Name,
    string? Description,
    string AvatarUrl,
    string? ModelDeploymentId);