using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

/// <summary>
/// Deep coverage for <see cref="TranscribeNotebookFileMarkdownHandler"/>: file resolution,
/// support/size gating, success, empty, and failure outcomes. Transcription is mocked via
/// <see cref="ITranscriptionAdapter"/>; files are read from a real temporary storage root.
/// </summary>
[TestClass]
public sealed class TranscribeNotebookFileMarkdownHandlerDeepTests
{
    [TestMethod]
    public async Task HandleAsync_FileMissingOnDisk_MarksFailed()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-missing-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");

        var handler = CreateHandler(options, new Mock<ITranscriptionAdapter>().Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Failed);
    }

    [TestMethod]
    public async Task HandleAsync_UnsupportedType_MarksSkipped()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-unsupported-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1, 2, 3 });

        var transcription = new Mock<ITranscriptionAdapter>();
        transcription.Setup(x => x.IsAudioOrVideoSupported("talk.mp3", It.IsAny<string>())).Returns(false);

        var handler = CreateHandler(options, transcription.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Skipped);
    }

    [TestMethod]
    public async Task HandleAsync_FileTooLarge_MarksSkipped()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-toolarge-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1, 2, 3 });

        var transcription = new Mock<ITranscriptionAdapter>();
        transcription.Setup(x => x.IsAudioOrVideoSupported("talk.mp3", It.IsAny<string>())).Returns(true);
        transcription.Setup(x => x.IsFileSizeSupported(It.IsAny<long>())).Returns(false);

        var handler = CreateHandler(options, transcription.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Skipped);
    }

    [TestMethod]
    public async Task HandleAsync_SuccessfulTranscription_WritesMarkdown_AndEnqueuesIndexing()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-success-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1, 2, 3 });

        var transcription = new Mock<ITranscriptionAdapter>();
        transcription.Setup(x => x.IsAudioOrVideoSupported("talk.mp3", It.IsAny<string>())).Returns(true);
        transcription.Setup(x => x.IsFileSizeSupported(It.IsAny<long>())).Returns(true);
        transcription.Setup(x => x.TranscribeToMarkdownAsync(It.IsAny<Stream>(), "talk.mp3", "audio/mpeg", It.IsAny<CancellationToken>()))
            .ReturnsAsync("transcribed text");
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();

        var handler = CreateHandler(options, transcription.Object, queue, storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        queue.Enqueued.Should().ContainSingle(e => e.JobType == "IndexNotebookMarkdownShadow");

        await using var verify = new ApplicationDbContext(options);
        var shadow = await verify.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
        shadow.Status.Should().Be(MarkdownExtractionStatus.Completed);
        File.Exists(shadow.StoragePath).Should().BeTrue();
        (await File.ReadAllTextAsync(shadow.StoragePath)).Should().Be("transcribed text");
    }

    [TestMethod]
    public async Task HandleAsync_EmptyTranscription_MarksSkipped()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-empty-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1 });

        var transcription = new Mock<ITranscriptionAdapter>();
        transcription.Setup(x => x.IsAudioOrVideoSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        transcription.Setup(x => x.IsFileSizeSupported(It.IsAny<long>())).Returns(true);
        transcription.Setup(x => x.TranscribeToMarkdownAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        var handler = CreateHandler(options, transcription.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Skipped);
    }

    [TestMethod]
    public async Task HandleAsync_TranscriptionThrows_MarksFailed()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-throw-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1 });

        var transcription = new Mock<ITranscriptionAdapter>();
        transcription.Setup(x => x.IsAudioOrVideoSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        transcription.Setup(x => x.IsFileSizeSupported(It.IsAny<long>())).Returns(true);
        transcription.Setup(x => x.TranscribeToMarkdownAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transcribe boom"));

        var handler = CreateHandler(options, transcription.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.RetryableTransient);

        await using var verify = new ApplicationDbContext(options);
        var shadow = await verify.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
        shadow.Status.Should().Be(MarkdownExtractionStatus.Pending);
    }

    [TestMethod]
    public async Task HandleAsync_PermanentMediaFailure_MarksFailedWithoutRetryClass()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-permanent-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1 });

        var transcription = new Mock<ITranscriptionAdapter>();
        transcription.Setup(x => x.IsAudioOrVideoSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        transcription.Setup(x => x.IsFileSizeSupported(It.IsAny<long>())).Returns(true);
        transcription.Setup(x => x.TranscribeToMarkdownAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Media extraction API failed (400): output contains no stream"));

        var handler = CreateHandler(options, transcription.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
        (await GetShadowStatusAsync(options, notebookFileId)).Should().Be(MarkdownExtractionStatus.Failed);
    }

    [TestMethod]
    public async Task HandleAsync_CompletedShadow_IsIdempotent()
    {
        using var storage = new TempStorage();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-deep-completed-{Guid.NewGuid():N}");
        var notebookFileId = await SeedAsync(options, "talk.mp3");
        storage.WriteNotebookFile("test-project", "test-notebook", "talk.mp3", new byte[] { 1 });

        await using (var seed = new ApplicationDbContext(options))
        {
            var shadow = await seed.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
            shadow.Status = MarkdownExtractionStatus.Completed;
            shadow.ContentHash = "done";
            shadow.StoragePath = "/tmp/done.md";
            await seed.SaveChangesAsync();
        }

        var transcription = new Mock<ITranscriptionAdapter>(MockBehavior.Strict);
        var handler = CreateHandler(options, transcription.Object, new BackgroundJobTestHelpers.CapturingJobQueueService(), storage.Root);

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        transcription.VerifyNoOtherCalls();
    }

    private static async Task<Guid> SeedAsync(DbContextOptions<ApplicationDbContext> options, string relativePath)
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
        seed.NotebookFileMarkdownShadows.Add(new NotebookFileMarkdownShadow
        {
            OriginalNotebookFileId = notebookFileId,
            ContentHash = string.Empty,
            StoragePath = string.Empty,
            FileSize = 0,
            Status = MarkdownExtractionStatus.Pending
        });
        await seed.SaveChangesAsync();
        return notebookFileId;
    }

    private static async Task<MarkdownExtractionStatus> GetShadowStatusAsync(DbContextOptions<ApplicationDbContext> options, Guid notebookFileId)
    {
        await using var verify = new ApplicationDbContext(options);
        var shadow = await verify.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == notebookFileId);
        return shadow.Status;
    }

    private static TranscribeNotebookFileMarkdownHandler CreateHandler(
        DbContextOptions<ApplicationDbContext> options,
        ITranscriptionAdapter transcription,
        BackgroundJobTestHelpers.CapturingJobQueueService queue,
        string storageRoot) =>
        new(
            NullLogger<TranscribeNotebookFileMarkdownHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            transcription,
            queue,
            BackgroundJobTestHelpers.CreateConfiguration(storageRoot));

    private sealed class TempStorage : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "guideants-transcribe-" + Guid.NewGuid().ToString("N"));

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
