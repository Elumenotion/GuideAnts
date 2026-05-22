namespace GuideAntsApi.BackgroundJobs.Options;

public static class ServiceProviderIds
{
    public const string EmbeddingsAzureOpenAiEmbedding = "Embeddings.AzureOpenAI.Embedding";
    public const string EmbeddingsLocalEmbHttp = "Embeddings.LocalEmb.Http";

    public const string DocumentIntelligenceAzure = "DocumentIntelligence.Azure.DocumentIntelligence";
    public const string DocumentIntelligenceLocalDoclingHttp = "DocumentIntelligence.LocalDocling.Http";
}

public sealed class LocalServiceHostsOptions
{
    public const string SectionName = "LocalServiceHosts";

    public string EmbeddingsBaseUrl { get; set; } = string.Empty;
    public string DocumentIntelligenceBaseUrl { get; set; } = string.Empty;
}
