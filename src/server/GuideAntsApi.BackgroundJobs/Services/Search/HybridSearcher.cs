using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GuideAntsApi.DataModel;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;

namespace GuideAntsApi.BackgroundJobs.Services.Search;

internal sealed class HybridSearcher(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEmbeddingService embeddingService,
    ILogger<HybridSearcher> log) : IHybridSearcher
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory = dbFactory;
    private readonly IEmbeddingService _embeddingService = embeddingService;
    private readonly ILogger<HybridSearcher> _log = log;

    public async Task<IReadOnlyList<HybridSearchResult>> SearchAsync(
        string query,
        string indexName,
        Guid? notebookId = null,
        int maxResults = 5,
        double vectorWeight = 0.7,
        double textWeight = 0.3,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<HybridSearchResult>();

        // Get query embedding for vector search
        var queryEmbeddings = await _embeddingService.GetEmbeddingsAsync([query], EmbeddingPurpose.Query, ct);
        var queryVector = queryEmbeddings[0];

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        
        try
        {
            // Use CONTAINSTABLE for ranked full-text search combined with vector search
            // CONTAINSTABLE returns RANK scores from 0-1000, we normalize to 0-1
            // We fetch more candidates from full-text search to ensure good hybrid results
            var candidateMultiplier = 2;
            var candidateLimit = maxResults * candidateMultiplier;
            
            // SQL Server vector type can be constructed from JSON array string
            var vectorJson = System.Text.Json.JsonSerializer.Serialize(queryVector);
            
            // CONTAINSTABLE requires specific syntax - wrap query in double quotes for phrase search
            // This avoids issues with reserved words and special characters
            // Escape double quotes in the query first, then escape single quotes for SQL string embedding
            var ftQuery = $"\"{query.Replace("\"", "\"\"")}\"";
            var escapedFtQuery = ftQuery.Replace("'", "''");
            
            // Build the hybrid search query using CONTAINSTABLE for text ranking
            // and vector_distance for semantic similarity
            // Construct the vector from JSON array literal since varbinary casting is not allowed
            var sql = $@"
                DECLARE @queryVec vector(1536) = CAST('{vectorJson}' AS vector(1536));
                
                WITH TextScores AS (
                    SELECT 
                        dc.Id,
                        dc.Content,
                        dc.ContentFileId,
                        dc.NotebookFileId,
                        dc.ChunkIndex,
                        dc.IndexName,
                        dc.Embedding,
                        CAST(COALESCE(ft.[RANK], 0) AS FLOAT) / 1000.0 AS TextScore
                    FROM DocumentChunks dc
                    LEFT JOIN CONTAINSTABLE(DocumentChunks, Content, '{escapedFtQuery}', {candidateLimit}) ft 
                        ON dc.Id = ft.[KEY]
                    WHERE dc.IndexName = @indexName
                        AND (@notebookId IS NULL OR dc.NotebookId = @notebookId)
                )
                SELECT TOP (@maxResults)
                    Id AS ChunkId,
                    Content,
                    ContentFileId,
                    NotebookFileId,
                    ChunkIndex,
                    IndexName,
                    CAST((1.0 - vector_distance('cosine', Embedding, @queryVec)) AS FLOAT) AS VectorScore,
                    CAST(TextScore AS FLOAT) AS TextScore,
                    CAST((@vectorWeight * (1.0 - vector_distance('cosine', Embedding, @queryVec))) 
                        + (@textWeight * TextScore) AS FLOAT) AS Score
                FROM TextScores
                WHERE TextScore > 0 OR vector_distance('cosine', Embedding, @queryVec) < 1.0
                ORDER BY Score DESC";

            var parameters = new[]
            {
                new SqlParameter("@indexName", System.Data.SqlDbType.NVarChar, 255) { Value = indexName },
                new SqlParameter("@notebookId", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)notebookId ?? DBNull.Value },
                new SqlParameter("@maxResults", System.Data.SqlDbType.Int) { Value = maxResults },
                new SqlParameter("@vectorWeight", System.Data.SqlDbType.Float) { Value = vectorWeight },
                new SqlParameter("@textWeight", System.Data.SqlDbType.Float) { Value = textWeight }
            };

            // Execute raw SQL and materialize results
            var results = new List<HybridSearchResult>();
            
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);
            
            await context.Database.OpenConnectionAsync(ct);
            
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new HybridSearchResult
                {
                    ChunkId = reader.GetGuid(0),
                    Content = reader.GetString(1),
                    ContentFileId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    NotebookFileId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    ChunkIndex = reader.GetInt32(4),
                    IndexName = reader.GetString(5),
                    VectorScore = reader.GetDouble(6),
                    TextScore = reader.GetDouble(7),
                    Score = reader.GetDouble(8)
                });
            }
            
            _log.LogInformation(
                "Hybrid search returned {Count} results (vector: {VectorWeight:F2}, text: {TextWeight:F2}) for query: {Query}", 
                results.Count, vectorWeight, textWeight, query);
                
            return results;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hybrid search failed for query: {Query}", query);
            return Array.Empty<HybridSearchResult>();
        }
    }
}
