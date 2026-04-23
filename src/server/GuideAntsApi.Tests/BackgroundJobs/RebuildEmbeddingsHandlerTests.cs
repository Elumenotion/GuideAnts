using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class RebuildEmbeddingsHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_RegeneratesEmbeddings_FromDocumentChunks_WithoutDeletingOrRequeueingShadows()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"rebuild-handler-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var contentCompletedId = Guid.NewGuid();
        var contentPendingId = Guid.NewGuid();
        var notebookCompletedId = Guid.NewGuid();
        var assistantCompletedId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = "idx",
                Content = "chunk-one",
                ChunkIndex = 0,
                Embedding = [0.1f]
            });
            seed.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = "idx",
                Content = "chunk-two",
                ChunkIndex = 1,
                Embedding = [0.2f]
            });

            seed.ContentFileMarkdownShadows.AddRange(
                new ContentFileMarkdownShadow
                {
                    OriginalContentFileVersionId = contentCompletedId,
                    ContentHash = "completed",
                    StoragePath = "completed.md",
                    FileSize = 1,
                    Status = MarkdownExtractionStatus.Completed,
                    IsIndexed = true
                },
                new ContentFileMarkdownShadow
                {
                    OriginalContentFileVersionId = contentPendingId,
                    ContentHash = "pending",
                    StoragePath = "pending.md",
                    FileSize = 1,
                    Status = MarkdownExtractionStatus.Pending,
                    IsIndexed = true
                });

            seed.NotebookFileMarkdownShadows.Add(new NotebookFileMarkdownShadow
            {
                OriginalNotebookFileId = notebookCompletedId,
                ContentHash = "nb",
                StoragePath = "nb.md",
                FileSize = 1,
                Status = MarkdownExtractionStatus.Completed,
                IsIndexed = true
            });

            seed.AssistantFileMarkdownShadows.Add(new AssistantFileMarkdownShadow
            {
                OriginalAssistantFileId = assistantCompletedId,
                ContentHash = "assistant",
                StoragePath = "assistant.md",
                FileSize = 1,
                Status = MarkdownExtractionStatus.Completed,
                IsIndexed = true
            });

            await seed.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(options);
        var embeddings = new FakeEmbeddingService();
        var handler = new RebuildEmbeddingsHandler(
            NullLogger<RebuildEmbeddingsHandler>.Instance,
            factory,
            embeddings);

        var success = await handler.HandleAsync(new RebuildEmbeddingsJob(), CancellationToken.None);
        success.Should().BeTrue();

        await using var verify = new ApplicationDbContext(options);
        var chunks = await verify.DocumentChunks
            .OrderBy(x => x.ChunkIndex)
            .ToListAsync();

        chunks.Should().HaveCount(2);
        chunks[0].Content.Should().Be("chunk-one");
        chunks[0].Embedding.Should().BeEquivalentTo([9f, 1f]);
        chunks[1].Content.Should().Be("chunk-two");
        chunks[1].Embedding.Should().BeEquivalentTo([9f, 1f]);

        embeddings.CallCount.Should().Be(1);
        embeddings.LastPurpose.Should().Be(EmbeddingPurpose.Document);
        embeddings.LastInputs.Should().BeEquivalentTo(["chunk-one", "chunk-two"]);

        var contentCompleted = await verify.ContentFileMarkdownShadows
            .SingleAsync(x => x.OriginalContentFileVersionId == contentCompletedId);
        contentCompleted.IsIndexed.Should().BeTrue();

        var contentPending = await verify.ContentFileMarkdownShadows
            .SingleAsync(x => x.OriginalContentFileVersionId == contentPendingId);
        contentPending.IsIndexed.Should().BeTrue();

        var notebookCompleted = await verify.NotebookFileMarkdownShadows
            .SingleAsync(x => x.OriginalNotebookFileId == notebookCompletedId);
        notebookCompleted.IsIndexed.Should().BeTrue();

        var assistantCompleted = await verify.AssistantFileMarkdownShadows
            .SingleAsync(x => x.OriginalAssistantFileId == assistantCompletedId);
        assistantCompleted.IsIndexed.Should().BeTrue();
    }

    [TestMethod]
    public async Task HandleAsync_ClearsExistingVectors_BeforeRegenerationCalls()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"rebuild-handler-clear-first-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = "idx",
                Content = "chunk-one",
                ChunkIndex = 0,
                Embedding = [42f, 42f]
            });
            await seed.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(options);
        var embeddings = new ThrowingEmbeddingService();
        var handler = new RebuildEmbeddingsHandler(
            NullLogger<RebuildEmbeddingsHandler>.Instance,
            factory,
            embeddings);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => handler.HandleAsync(new RebuildEmbeddingsJob(), CancellationToken.None));

        await using var verify = new ApplicationDbContext(options);
        var chunk = await verify.DocumentChunks.SingleAsync();
        chunk.Embedding.Length.Should().Be(1536);
        chunk.Embedding.Should().OnlyContain(x => x == 0f);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public int CallCount { get; private set; }
        public EmbeddingPurpose LastPurpose { get; private set; }
        public List<string> LastInputs { get; private set; } = [];

        public Task<float[][]> GetEmbeddingsAsync(
            IEnumerable<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPurpose = purpose;
            LastInputs = texts.ToList();

            // Deterministic vector for assertions: [contentLength, purposeMarker]
            var vectors = LastInputs
                .Select(x => new[] { (float)x.Length, purpose == EmbeddingPurpose.Document ? 1f : 0f })
                .ToArray();

            return Task.FromResult(vectors);
        }
    }

    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public Task<float[][]> GetEmbeddingsAsync(
            IEnumerable<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            _ = texts;
            _ = purpose;
            throw new InvalidOperationException("intentional test failure");
        }
    }
}
