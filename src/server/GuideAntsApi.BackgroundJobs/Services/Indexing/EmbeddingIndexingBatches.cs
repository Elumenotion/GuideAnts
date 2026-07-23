using GuideAntsApi.BackgroundJobs.Services.Embeddings;

namespace GuideAntsApi.BackgroundJobs.Services.Indexing;

internal static class EmbeddingIndexingBatches
{
    /// <summary>
    /// Matches <see cref="Jobs.RebuildEmbeddingsHandler"/> batch sizing so initial indexing
    /// and rebuild paths stay aligned.
    /// </summary>
    public const int MaxChunksPerBatch = 64;

    /// <summary>
    /// Local llama-server embeddings use a 131072-token context; keep headroom for Qwen prefixes.
    /// </summary>
    public const int MaxEstimatedTokensPerBatch = 96_000;

    public static IReadOnlyList<(int Start, int Count)> GetBatchRanges(IReadOnlyList<string> chunks)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<(int Start, int Count)>();
        }

        var ranges = new List<(int Start, int Count)>();
        var batchStart = 0;
        while (batchStart < chunks.Count)
        {
            var batchEnd = batchStart;
            var estimatedTokens = 0;
            while (batchEnd < chunks.Count
                   && batchEnd - batchStart < MaxChunksPerBatch
                   && estimatedTokens + EstimateTokens(chunks[batchEnd]) <= MaxEstimatedTokensPerBatch)
            {
                estimatedTokens += EstimateTokens(chunks[batchEnd]);
                batchEnd++;
            }

            if (batchEnd == batchStart)
            {
                batchEnd = batchStart + 1;
            }

            ranges.Add((batchStart, batchEnd - batchStart));
            batchStart = batchEnd;
        }

        return ranges;
    }

    public static async Task<float[][]> EmbedAllAsync(
        IEmbeddingService embeddings,
        IReadOnlyList<string> chunks,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var results = new float[chunks.Count][];
        foreach (var (start, count) in GetBatchRanges(chunks))
        {
            var batch = new string[count];
            for (var i = 0; i < count; i++)
            {
                batch[i] = chunks[start + i];
            }

            var vectors = await embeddings.GetEmbeddingsAsync(batch, purpose, cancellationToken);
            if (vectors.Length != count)
            {
                throw new InvalidOperationException(
                    $"Embedding service returned {vectors.Length} vectors for batch size {count}.");
            }

            Array.Copy(vectors, 0, results, start, count);
        }

        return results;
    }

    internal static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        // Conservative char-based estimate so dense/code text stays under server ctx limits.
        return Math.Max(1, (text.Length + 2) / 3);
    }
}
