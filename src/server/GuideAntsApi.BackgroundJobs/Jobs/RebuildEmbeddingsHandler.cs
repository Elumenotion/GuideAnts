using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class RebuildEmbeddingsHandler(
    ILogger<RebuildEmbeddingsHandler> logger,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEmbeddingService embeddingService) : JobHandlerBase<RebuildEmbeddingsJob>(logger)
{
    private const int BatchSize = 64;
    private const int VectorDimensions = 1536;

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory = dbFactory;
    private readonly IEmbeddingService _embeddingService = embeddingService;

    public override string JobType => nameof(RebuildEmbeddingsJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(RebuildEmbeddingsJob payload, CancellationToken cancellationToken)
    {
        _ = payload;

        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var totalChunkCount = await context.DocumentChunks.CountAsync(cancellationToken);

        if (totalChunkCount == 0)
        {
            Logger.LogInformation("RebuildEmbeddings complete: no DocumentChunks found.");
            return true;
        }

        // First pass: clear vectors so a model change never leaves stale embeddings behind.
        var clearedCount = 0;
        for (var offset = 0; offset < totalChunkCount; offset += BatchSize)
        {
            var clearBatch = await context.DocumentChunks
                .OrderBy(c => c.Id)
                .Skip(offset)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (clearBatch.Count == 0)
            {
                break;
            }

            foreach (var chunk in clearBatch)
            {
                chunk.Embedding = new float[VectorDimensions];
            }

            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            clearedCount += clearBatch.Count;
        }

        var regeneratedCount = 0;

        for (var offset = 0; offset < totalChunkCount; offset += BatchSize)
        {
            var batch = await context.DocumentChunks
                .OrderBy(c => c.Id)
                .Skip(offset)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            var embeddings = await _embeddingService.GetEmbeddingsAsync(
                batch.Select(c => c.Content),
                EmbeddingPurpose.Document,
                cancellationToken);

            if (embeddings.Length != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding service returned {embeddings.Length} vectors for batch size {batch.Count}.");
            }

            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].Embedding = embeddings[i];
            }

            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            regeneratedCount += batch.Count;
        }

        Logger.LogInformation(
            "RebuildEmbeddings complete: clearedEmbeddings={ClearedEmbeddings} regeneratedEmbeddings={RegeneratedEmbeddings} totalChunks={TotalChunks} batchSize={BatchSize}",
            clearedCount,
            regeneratedCount,
            totalChunkCount,
            BatchSize);

        return true;
    }
}
