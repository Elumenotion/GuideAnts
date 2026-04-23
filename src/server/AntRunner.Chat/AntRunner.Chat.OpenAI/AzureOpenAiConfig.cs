namespace AntRunner.Chat.OpenAI;

/// <summary>
/// Gets the configuration settings for connecting to the Azure OpenAI service.
/// </summary>
public static class AzureOpenAiConfigFactory
{
    private static readonly AzureOpenAiConfig? AzureOpenAiConfig;

    static AzureOpenAiConfigFactory()
    {
        AzureOpenAiConfig = new AzureOpenAiConfig
        {
            ResourceName = Environment.GetEnvironmentVariable("AZURE_OPENAI_RESOURCE"),
            ApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            ApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"),
            DeploymentId = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
        };
    }

#pragma warning disable CS8603 // Possible null reference return.
    public static AzureOpenAiConfig Get() { return AzureOpenAiConfig; } // Set in constructor.
#pragma warning restore CS8603 // Possible null reference return.
}

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
