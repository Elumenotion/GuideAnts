using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class EmbeddingIndexingBatchesTests
{
    [TestMethod]
    public void GetBatchRanges_ReturnsEmpty_ForNoChunks()
    {
        EmbeddingIndexingBatches.GetBatchRanges(Array.Empty<string>()).Should().BeEmpty();
    }

    [TestMethod]
    public void GetBatchRanges_ReturnsSingleBatch_ForSmallDocument()
    {
        var chunks = new[] { "alpha", "beta", "gamma" };

        EmbeddingIndexingBatches.GetBatchRanges(chunks).Should().Equal([(0, 3)]);
    }

    [TestMethod]
    public void GetBatchRanges_SplitsAtMaxChunksPerBatch()
    {
        var chunks = Enumerable.Range(0, EmbeddingIndexingBatches.MaxChunksPerBatch + 1)
            .Select(i => $"chunk-{i}")
            .ToArray();

        EmbeddingIndexingBatches.GetBatchRanges(chunks).Should().Equal([
            (0, EmbeddingIndexingBatches.MaxChunksPerBatch),
            (EmbeddingIndexingBatches.MaxChunksPerBatch, 1)
        ]);
    }

    [TestMethod]
    public void GetBatchRanges_SplitsBeforeTokenBudgetIsExceeded()
    {
        var denseChunk = new string('x', 300_000);
        var chunks = new[] { denseChunk, denseChunk, "tail" };

        var ranges = EmbeddingIndexingBatches.GetBatchRanges(chunks);

        ranges.Should().HaveCount(3);
        ranges[0].Count.Should().Be(1);
        ranges[1].Count.Should().Be(1);
        ranges[2].Should().Be((2, 1));
    }

    [TestMethod]
    public async Task EmbedAllAsync_CallsEmbeddingServiceOncePerBatch()
    {
        var chunks = Enumerable.Range(0, EmbeddingIndexingBatches.MaxChunksPerBatch + 2)
            .Select(i => $"chunk-{i}")
            .ToArray();
        var service = new CountingEmbeddingService();

        var vectors = await EmbeddingIndexingBatches.EmbedAllAsync(
            service,
            chunks,
            EmbeddingPurpose.Document);

        service.CallCount.Should().Be(2);
        vectors.Should().HaveCount(chunks.Length);
        vectors.Should().OnlyContain(v => v.Length == 1 && Math.Abs(v[0] - 0.5f) < 0.001f);
    }

    private sealed class CountingEmbeddingService : IEmbeddingService
    {
        public int CallCount { get; private set; }

        public Task<float[][]> GetEmbeddingsAsync(
            IEnumerable<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(texts.Select(_ => new[] { 0.5f }).ToArray());
        }
    }
}
