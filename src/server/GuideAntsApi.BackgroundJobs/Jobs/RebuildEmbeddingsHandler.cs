using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class RebuildEmbeddingsHandler(
    ILogger<RebuildEmbeddingsHandler> logger,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEmbeddingService embeddingService,
    IJobQueueService jobQueueService) : JobHandlerBase<RebuildEmbeddingsJob>(logger)
{
    private const int BatchSize = 64;
    private const int VectorDimensions = 1536;

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory = dbFactory;
    private readonly IEmbeddingService _embeddingService = embeddingService;
    private readonly IJobQueueService _jobQueueService = jobQueueService;

    public override string JobType => nameof(RebuildEmbeddingsJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(RebuildEmbeddingsJob payload, CancellationToken cancellationToken)
    {
        _ = payload;

        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (assistantQueued, notebookQueued, contentQueued) =
            await EnqueueMissingIndexJobsAsync(context, cancellationToken);

        var totalChunkCount = await context.DocumentChunks.CountAsync(cancellationToken);

        if (totalChunkCount == 0)
        {
            Logger.LogInformation(
                "RebuildEmbeddings complete: no DocumentChunks found. queuedBackfillJobs assistant={AssistantQueued} notebook={NotebookQueued} content={ContentQueued}",
                assistantQueued,
                notebookQueued,
                contentQueued);
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
            "RebuildEmbeddings complete: clearedEmbeddings={ClearedEmbeddings} regeneratedEmbeddings={RegeneratedEmbeddings} totalChunks={TotalChunks} batchSize={BatchSize} queuedBackfillJobs assistant={AssistantQueued} notebook={NotebookQueued} content={ContentQueued}",
            clearedCount,
            regeneratedCount,
            totalChunkCount,
            BatchSize,
            assistantQueued,
            notebookQueued,
            contentQueued);

        return true;
    }

    private async Task<(int AssistantQueued, int NotebookQueued, int ContentQueued)> EnqueueMissingIndexJobsAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var inFlightStatuses = new[] { JobStatus.Pending, JobStatus.Processing };
        var indexJobTypes = new[]
        {
            nameof(IndexAssistantFileMarkdownShadowJob).Replace("Job", string.Empty),
            nameof(IndexNotebookMarkdownShadowJob).Replace("Job", string.Empty),
            nameof(IndexContentMarkdownShadowJob).Replace("Job", string.Empty)
        };

        var inFlightJobKeys = new HashSet<string>(StringComparer.Ordinal);
        var inFlightJobs = await context.JobQueue
            .AsNoTracking()
            .Where(j => indexJobTypes.Contains(j.JobType) && inFlightStatuses.Contains(j.Status))
            .Select(j => new { j.JobType, j.PayloadJson })
            .ToListAsync(cancellationToken);

        foreach (var job in inFlightJobs)
        {
            inFlightJobKeys.Add(BuildJobKey(job.JobType, job.PayloadJson));
        }

        var assistantChunkIds = (await context.DocumentChunks
            .AsNoTracking()
            .Where(c => c.AssistantFileId != null)
            .Select(c => c.AssistantFileId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var notebookChunkIds = (await context.DocumentChunks
            .AsNoTracking()
            .Where(c => c.NotebookFileId != null)
            .Select(c => c.NotebookFileId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var contentChunkIds = (await context.DocumentChunks
            .AsNoTracking()
            .Where(c => c.ContentFileId != null)
            .Select(c => c.ContentFileId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        int assistantQueued = 0;
        int notebookQueued = 0;
        int contentQueued = 0;

        var assistantCandidates = await context.AssistantFileMarkdownShadows
            .AsNoTracking()
            .Where(s => s.Status == MarkdownExtractionStatus.Completed)
            .Select(s => new
            {
                s.OriginalAssistantFileId,
                s.IsIndexed
            })
            .ToListAsync(cancellationToken);

        foreach (var shadow in assistantCandidates)
        {
            if (shadow.IsIndexed && assistantChunkIds.Contains(shadow.OriginalAssistantFileId))
            {
                continue;
            }

            var payload = new IndexAssistantFileMarkdownShadowJob(shadow.OriginalAssistantFileId);
            var jobType = nameof(IndexAssistantFileMarkdownShadowJob).Replace("Job", string.Empty);
            var payloadJson = JsonSerializer.Serialize(payload);
            var key = BuildJobKey(jobType, payloadJson);
            if (!inFlightJobKeys.Add(key))
            {
                continue;
            }

            await _jobQueueService.EnqueueAsync(jobType, payload, ct: cancellationToken);
            assistantQueued++;
        }

        var notebookCandidates = await context.NotebookFileMarkdownShadows
            .AsNoTracking()
            .Include(s => s.OriginalFile)
            .Where(s => s.Status == MarkdownExtractionStatus.Completed && s.OriginalFile != null)
            .Select(s => new
            {
                s.OriginalNotebookFileId,
                s.IsIndexed
            })
            .ToListAsync(cancellationToken);

        foreach (var shadow in notebookCandidates)
        {
            if (shadow.IsIndexed && notebookChunkIds.Contains(shadow.OriginalNotebookFileId))
            {
                continue;
            }

            var payload = new IndexNotebookMarkdownShadowJob(shadow.OriginalNotebookFileId);
            var jobType = nameof(IndexNotebookMarkdownShadowJob).Replace("Job", string.Empty);
            var payloadJson = JsonSerializer.Serialize(payload);
            var key = BuildJobKey(jobType, payloadJson);
            if (!inFlightJobKeys.Add(key))
            {
                continue;
            }

            await _jobQueueService.EnqueueAsync(jobType, payload, ct: cancellationToken);
            notebookQueued++;
        }

        var contentCandidates = await context.ContentFileMarkdownShadows
            .AsNoTracking()
            .Include(s => s.OriginalVersion)
            .Where(s => s.Status == MarkdownExtractionStatus.Completed && s.OriginalVersion != null)
            .Select(s => new
            {
                s.OriginalContentFileVersionId,
                ContentFileId = s.OriginalVersion.ContentFileId,
                s.IsIndexed
            })
            .ToListAsync(cancellationToken);

        foreach (var shadow in contentCandidates)
        {
            if (shadow.IsIndexed && contentChunkIds.Contains(shadow.ContentFileId))
            {
                continue;
            }

            var payload = new IndexContentMarkdownShadowJob(shadow.OriginalContentFileVersionId);
            var jobType = nameof(IndexContentMarkdownShadowJob).Replace("Job", string.Empty);
            var payloadJson = JsonSerializer.Serialize(payload);
            var key = BuildJobKey(jobType, payloadJson);
            if (!inFlightJobKeys.Add(key))
            {
                continue;
            }

            await _jobQueueService.EnqueueAsync(jobType, payload, ct: cancellationToken);
            contentQueued++;
        }

        return (assistantQueued, notebookQueued, contentQueued);
    }

    private static string BuildJobKey(string jobType, string payloadJson) => $"{jobType}:{payloadJson}";
}
