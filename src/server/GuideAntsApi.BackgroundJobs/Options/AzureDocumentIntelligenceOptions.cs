namespace GuideAntsApi.BackgroundJobs.Options;

public sealed class AzureDocumentIntelligenceOptions
{
    public const string SectionName = "AzureDocumentIntelligence";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-11-30";
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 3;
}



