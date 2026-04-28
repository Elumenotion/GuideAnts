namespace AntRunner.Chat.OpenAI;

/// <summary>
/// Represents the configuration settings for connecting to the Azure OpenAI service.
/// </summary>
public record AzureOpenAiConfig
{
    /// <summary>
    /// The name of an Azure OpenAI Service
    /// </summary>
    public string? ResourceName { set; get; }

    /// <summary>
    /// The API key for the Azure OpenAI service.
    /// </summary>
    public string? ApiKey { set; get; }

    /// <summary>
    /// A valid API Version
    /// See https://learn.microsoft.com/en-us/azure/ai-services/openai/reference
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// The model deployment ID, e.g. "gpt-4o".
    /// </summary>
    public string? DeploymentId { get; set; }
}
