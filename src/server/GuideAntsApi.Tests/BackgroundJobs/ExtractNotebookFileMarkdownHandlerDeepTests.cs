using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

/// <summary>
/// Deep coverage for <see cref="ExtractNotebookFileMarkdownHandler"/>: shadow recovery,
/// physical-file resolution, unsupported-type routing to transcription, success/empty/error
/// extraction outcomes. The document intelligence call is mocked; files are read from a real
/// temporary storage root.
/// </summary>
[TestClass]
public sealed class ExtractNotebookFileMarkdownHandlerDeepTests
{
    [TestMethod]
    public async Task HandleAsync_FileMissingOnDisk_MarksFailed()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-deep-missing-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "report.pdf", createShadow: true);

        var handler = CreateHandler(options, new Mock<IDocumentIntelligenceService>().Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Failed);
    }

    [TestMethod]
    public async Task HandleAsync_UnsupportedFileType_RoutesToTranscriptionQueue()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-deep-unsupported-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "audio.mp3", createShadow: true);
        storage.WriteNotebookFile("test-project", "test-notebook", "audio.mp3", new byte[] { 1, 2, 3 });

        var docIntel = new Mock<IDocumentIntelligenceService>();
        docIntel.Setup(x => x.IsFileTypeSupported("audio.mp3", It.IsAny<string>())).Returns(false);
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();

        var handler = CreateHandler(options, docIntel.Object, queue, storage.Root);

        var result = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        queue.Enqueued.Should().ContainSingle(e => e.JobType == "TranscribeNotebookFileMarkdown");
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Pending);
    }

    [TestMethod]
    public async Task HandleAsync_SuccessfulExtraction_WritesMarkdown_AndEnqueuesIndexing()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-deep-success-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "report.pdf", createShadow: true);
        storage.WriteNotebookFile("test-project", "test-notebook", "report.pdf", new byte[] { 1, 2, 3 });

        var docIntel = new Mock<IDocumentIntelligenceService>();
        docIntel.Setup(x => x.IsFileTypeSupported("report.pdf", It.IsAny<string>())).Returns(true);
        docIntel.Setup(x => x.ExtractMarkdownAsync(It.IsAny<Stream>(), "report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Extracted Markdown");
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();

        var handler = CreateHandler(options, docIntel.Object, queue, storage.Root);

        var result = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        queue.Enqueued.Should().ContainSingle(e => e.JobType == "IndexNotebookMarkdownShadow");

        await using var verify = new ApplicationDbContext(options);
        var shadow = await verify.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
        shadow.Status.Should().Be(MarkdownExtractionStatus.Completed);
        shadow.ContentHash.Should().NotBeNullOrEmpty();
        File.Exists(shadow.StoragePath).Should().BeTrue();
        (await File.ReadAllTextAsync(shadow.StoragePath)).Should().Be("# Extracted Markdown");
    }

    [TestMethod]
    public async Task HandleAsync_EmptyExtraction_MarksSkipped()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-deep-empty-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "report.pdf", createShadow: true);
        storage.WriteNotebookFile("test-project", "test-notebook", "report.pdf", new byte[] { 1 });

        var docIntel = new Mock<IDocumentIntelligenceService>();
        docIntel.Setup(x => x.IsFileTypeSupported("report.pdf", It.IsAny<string>())).Returns(true);
        docIntel.Setup(x => x.ExtractMarkdownAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var handler = CreateHandler(options, docIntel.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Skipped);
    }

    [TestMethod]
    public async Task HandleAsync_ExtractionThrows_MarksFailed()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-deep-throw-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "report.pdf", createShadow: true);
        storage.WriteNotebookFile("test-project", "test-notebook", "report.pdf", new byte[] { 1 });

        var docIntel = new Mock<IDocumentIntelligenceService>();
        docIntel.Setup(x => x.IsFileTypeSupported("report.pdf", It.IsAny<string>())).Returns(true);
        docIntel.Setup(x => x.ExtractMarkdownAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("extraction boom"));

        var handler = CreateHandler(options, docIntel.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.RetryableTransient);

        await using var verify = new ApplicationDbContext(options);
        var shadow = await verify.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
        shadow.Status.Should().Be(MarkdownExtractionStatus.Failed);
        shadow.ErrorMessage.Should().Be("extraction boom");
    }

    [TestMethod]
    public async Task HandleAsync_RecoversMissingShadow_WhenNotebookFileExists()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-deep-recover-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "report.pdf", createShadow: false);
        storage.WriteNotebookFile("test-project", "test-notebook", "report.pdf", new byte[] { 1, 2 });

        var docIntel = new Mock<IDocumentIntelligenceService>();
        docIntel.Setup(x => x.IsFileTypeSupported("report.pdf", It.IsAny<string>())).Returns(true);
        docIntel.Setup(x => x.ExtractMarkdownAsync(It.IsAny<Stream>(), "report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Recovered");

        var handler = CreateHandler(options, docIntel.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Completed);
    }

    private static async Task<Guid> SeedAsync(DbContextOptions<ApplicationDbContext> options, string relativePath, bool createShadow)
    {
        var notebookFileId = Guid.NewGuid();
        await using var seed = new ApplicationDbContext(options);
        var (_, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
        var notebookFile = new NotebookFile
        {
            Id = notebookFileId,
            NotebookId = notebookId,
            RelativePath = relativePath,
            FileSize = 3,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash"
        };
        notebookFile.GenerateDocumentId(notebookId);
        seed.NotebookFiles.Add(notebookFile);
        if (createShadow)
        {
            seed.NotebookFileMarkdownShadows.Add(new NotebookFileMarkdownShadow
            {
                OriginalNotebookFileId = notebookFileId,
                ContentHash = string.Empty,
                StoragePath = string.Empty,
                FileSize = 0,
                Status = MarkdownExtractionStatus.Pending
            });
        }

        await seed.SaveChangesAsync();
        return notebookFileId;
    }

    private static async Task<MarkdownExtractionStatus> GetShadowStatusAsync(DbContextOptions<ApplicationDbContext> options, Guid notebookFileId)
    {
        await using var verify = new ApplicationDbContext(options);
        var shadow = await verify.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
        return shadow.Status;
    }

    private static ExtractNotebookFileMarkdownHandler CreateHandler(
        DbContextOptions<ApplicationDbContext> options,
        IDocumentIntelligenceService docIntel,
        BackgroundJobTestHelpers.CapturingJobQueueService queue,
        string storageRoot) =>
        new(
            NullLogger<ExtractNotebookFileMarkdownHandler>.Instance,
            docIntel,
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            BackgroundJobTestHelpers.CreateFactory(options),
            queue,
            BackgroundJobTestHelpers.CreateConfiguration(storageRoot));

    private sealed class TempStorage : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "guideants-extract-" + Guid.NewGuid().ToString("N"));

        public TempStorage() => Directory.CreateDirectory(Root);

        public void WriteNotebookFile(string projectSlug, string notebookSlug, string relativePath, byte[] content)
        {
            var path = Path.Combine(Root, projectSlug, notebookSlug, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
