using System.ComponentModel.DataAnnotations;

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
    public decimal? DailyChargeLimitUsd { get; set; }
    public decimal? BillingPeriodChargeLimitUsd { get; set; }
    /// <summary>
    /// Indicates whether an API key is configured for this published guide.
    /// The actual key is never returned - only shown once at creation/regeneration.
    /// </summary>
    public bool HasApiKey { get; set; }
}

public class PublishGuideDto
{
    public Guid ProjectId { get; set; }
    public int? RetentionPeriod { get; set; }
    public int? MaxUserMessageLength { get; set; }
    public int? MaxTurns { get; set; }
    public decimal? DailyChargeLimitUsd { get; set; }
    public decimal? BillingPeriodChargeLimitUsd { get; set; }
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
}

public class UpdatePublishedGuideDto
{
    public int? RetentionPeriod { get; set; }
    public int? MaxUserMessageLength { get; set; }
    public int? MaxTurns { get; set; }
    public decimal? DailyChargeLimitUsd { get; set; }
    public decimal? BillingPeriodChargeLimitUsd { get; set; }
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




