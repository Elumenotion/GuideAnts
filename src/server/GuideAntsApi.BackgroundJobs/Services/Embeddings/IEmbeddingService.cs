namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

public enum EmbeddingPurpose
{
    Document = 0,
    Query = 1
}

public interface IEmbeddingService
{
    Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default);
}
