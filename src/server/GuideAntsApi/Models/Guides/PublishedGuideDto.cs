using System.ComponentModel.DataAnnotations;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Models.Guides;

public class PublishedGuideDto
{
    public Guid Id { get; set; }
    public Guid GuideId { get; set; }
    public string GuideName { get; set; } = string.Empty;
    public Guid NotebookId { get; set; }
    public Guid ProjectId { get; set; }
    public DateTime Created { get; set; }
    public bool Active { get; set; }
    public int? RetentionPeriod { get; set; }
    public int? MaxUserMessageLength { get; set; }
    public int? MaxTurns { get; set; }
    public string? AuthValidationWebhookUrl { get; set; }
    public int? AuthWebhookTimeoutSeconds { get; set; }
    public string? FriendlyName { get; set; }
    public string? DisplayMode { get; set; }
    public bool CommandMode { get; set; }
    public bool ShowTurnNavigation { get; set; }
    public bool Collapsible { get; set; }
    public bool ShowConversationStarters { get; set; }
    public bool ShowAttachments { get; set; }
    public PublishedWireApiConfigDto? WireApiConfig { get; set; }
    public decimal? DailyChargeLimitUsd { get; set; }
    public decimal? BillingPeriodChargeLimitUsd { get; set; }
    public PublishedGuideAuthMode AuthMode { get; set; } = PublishedGuideAuthMode.Anonymous;
    /// <summary>
    /// Indicates whether an API key is configured for this published guide.
    /// The actual key is never returned - only shown once at creation/regeneration.
    /// </summary>
    public bool HasApiKey { get; set; }

    /// <summary>
    /// Whether MCP (Model Context Protocol) access is enabled for this published guide.
    /// </summary>
    public bool McpEnabled { get; set; }

    /// <summary>
    /// Client-facing description for MCP discovery. Describes what this guide does and how to use it.
    /// </summary>
    public string? McpDescription { get; set; }
}

public class PublishGuideDto
{
    public Guid ProjectId { get; set; }
    public int? RetentionPeriod { get; set; }
    public int? MaxUserMessageLength { get; set; }
    public int? MaxTurns { get; set; }
    public decimal? DailyChargeLimitUsd { get; set; }
    public decimal? BillingPeriodChargeLimitUsd { get; set; }
    public PublishedGuideAuthMode? AuthMode { get; set; }
    [StringLength(2048)]
    public string? AuthValidationWebhookUrl { get; set; }
    public int? AuthWebhookTimeoutSeconds { get; set; }
    [StringLength(100)]
    public string? FriendlyName { get; set; }
    [StringLength(20)]
    public string? DisplayMode { get; set; }
    public bool CommandMode { get; set; }
    public bool ShowTurnNavigation { get; set; }
    public bool Collapsible { get; set; }
    public bool ShowConversationStarters { get; set; }
    public bool ShowAttachments { get; set; }
    public PublishedWireApiConfigDto? WireApiConfig { get; set; }
    public bool McpEnabled { get; set; }
    [StringLength(2000)]
    public string? McpDescription { get; set; }
}

public class UpdatePublishedGuideDto
{
    public int? RetentionPeriod { get; set; }
    public int? MaxUserMessageLength { get; set; }
    public int? MaxTurns { get; set; }
    public decimal? DailyChargeLimitUsd { get; set; }
    public decimal? BillingPeriodChargeLimitUsd { get; set; }
    public PublishedGuideAuthMode? AuthMode { get; set; }
    [StringLength(2048)]
    public string? AuthValidationWebhookUrl { get; set; }
    public int? AuthWebhookTimeoutSeconds { get; set; }
    [StringLength(100)]
    public string? FriendlyName { get; set; }
    [StringLength(20)]
    public string? DisplayMode { get; set; }
    public bool CommandMode { get; set; }
    public bool ShowTurnNavigation { get; set; }
    public bool Collapsible { get; set; }
    public bool ShowConversationStarters { get; set; }
    public bool ShowAttachments { get; set; }
    public PublishedWireApiConfigDto? WireApiConfig { get; set; }
    public bool McpEnabled { get; set; }
    [StringLength(2000)]
    public string? McpDescription { get; set; }
}

public class PublishedWireApiConfigDto
{
    public bool Enabled { get; set; }
    [StringLength(64)]
    public string? Profile { get; set; }
    public PublishedWireApiEndpointFlagsDto? EndpointFlags { get; set; }
    public Dictionary<string, string>? AliasMap { get; set; }
    public PublishedWireApiMaxRequestSizesDto? MaxRequestSizes { get; set; }
}

public class PublishedWireApiEndpointFlagsDto
{
    public bool? Models { get; set; }
    public bool? ChatCompletions { get; set; }
    public bool? Responses { get; set; }
    public bool? Embeddings { get; set; }
    public bool? ImageGenerations { get; set; }
    public bool? AudioTranscriptions { get; set; }
    public bool? AudioSpeech { get; set; }
}

public class PublishedWireApiMaxRequestSizesDto
{
    public int? ChatCompletionsBytes { get; set; }
    public int? ResponsesBytes { get; set; }
    public int? EmbeddingsBytes { get; set; }
    public int? ImageGenerationsBytes { get; set; }
    public int? AudioTranscriptionsBytes { get; set; }
    public int? AudioSpeechBytes { get; set; }
}

/// <summary>
/// Response returned when generating or regenerating an API key.
/// The plaintext API key is only returned in this response and cannot be retrieved again.
/// </summary>
public class ApiKeyGenerationResultDto
{
    /// <summary>
    /// The plaintext API key. Store this securely - it cannot be retrieved again.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Warning message about the key being shown only once.
    /// </summary>
    public string Warning { get; set; } = "This API key will only be shown once. Store it securely - you will not be able to retrieve it again.";
}



