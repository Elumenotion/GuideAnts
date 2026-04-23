using AntRunner.Chat;

namespace GuideAntsApi.Models.Conversations;

public class PublishedGuideInvokeRequest
{
    public string Instructions { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? ModelDeploymentId { get; set; }
    public bool? UseAssistantDefinitionModel { get; set; }
    public List<AttachmentDto>? Attachments { get; set; }
    public Dictionary<string, string>? ExternalAuthTokens { get; set; }
    public string? ClientContext { get; set; }
}

public record PublishedGuideInvokeResponse(
    Guid ConversationId,
    Guid? AssistantMessageId,
    string? AssistantMessage,
    UsageResponse? Usage);
