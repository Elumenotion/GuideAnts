namespace GuideAntsApi.BackgroundJobs.Options;

public class AzureOpenAiEmbeddingOptions
{
    public const string SectionName = "AzureOpenAiEmbedding";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Deployment { get; set; } = string.Empty;
}
