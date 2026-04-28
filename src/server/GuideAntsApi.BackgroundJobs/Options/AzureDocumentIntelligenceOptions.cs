namespace GuideAntsApi.BackgroundJobs.Options;

public sealed class AzureDocumentIntelligenceOptions
{
    public const string SectionName = "AzureDocumentIntelligence";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}



