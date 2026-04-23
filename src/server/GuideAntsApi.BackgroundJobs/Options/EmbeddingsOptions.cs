namespace GuideAntsApi.BackgroundJobs.Options;

public sealed class EmbeddingsOptions
{
    public const string SectionName = "Embeddings";

    public int TimeoutSeconds { get; set; } = 300;
    public int LocalMinIntervalMs { get; set; } = 5000;
}
