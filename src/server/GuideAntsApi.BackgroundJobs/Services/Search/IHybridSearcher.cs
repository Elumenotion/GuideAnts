namespace GuideAntsApi.BackgroundJobs.Services.Search;

public interface IHybridSearcher
{
    Task<IReadOnlyList<HybridSearchResult>> SearchAsync(
        string query,
        string indexName,
        Guid? notebookId = null,
        int maxResults = 5,
        double vectorWeight = 0.7,
        double textWeight = 0.3,
        CancellationToken ct = default);
}

public class HybridSearchResult
{
    public Guid ChunkId { get; set; }
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public double VectorScore { get; set; }
    public double TextScore { get; set; }
    public Guid? ContentFileId { get; set; }
    public Guid? NotebookFileId { get; set; }
    public int ChunkIndex { get; set; }
    public string IndexName { get; set; } = string.Empty;
}
