using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class ExtractNotebookFileMarkdownHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Returns_false_when_notebook_file_does_not_exist()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-nb-missing-{Guid.NewGuid():N}");
        var handler = CreateHandler(options, new Mock<IDocumentIntelligenceService>().Object, new BackgroundJobTestHelpers.CapturingJobQueueService());

        var success = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeFalse();
    }

    [TestMethod]
    public async Task HandleAsync_Is_idempotent_when_shadow_already_completed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-nb-done-{Guid.NewGuid():N}");
        var notebookFileId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            var (_, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var notebookFile = new NotebookFile
            {
                Id = notebookFileId,
                NotebookId = notebookId,
                RelativePath = "doc.pdf",
                FileSize = 1,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash"
            };
            notebookFile.GenerateDocumentId(notebookId);
            seed.NotebookFiles.Add(notebookFile);
            seed.NotebookFileMarkdownShadows.Add(new NotebookFileMarkdownShadow
            {
                OriginalNotebookFileId = notebookFileId,
                ContentHash = "done",
                StoragePath = "done.md",
                FileSize = 1,
                Status = MarkdownExtractionStatus.Completed
            });
            await seed.SaveChangesAsync();
        }

        var docIntel = new Mock<IDocumentIntelligenceService>();
        var handler = CreateHandler(options, docIntel.Object, new BackgroundJobTestHelpers.CapturingJobQueueService());

        var success = await handler.HandleAsync(new ExtractNotebookFileMarkdownJob(notebookFileId), CancellationToken.None);

        success.Should().BeTrue();
        docIntel.VerifyNoOtherCalls();
    }

    private static ExtractNotebookFileMarkdownHandler CreateHandler(
        DbContextOptions<ApplicationDbContext> options,
        IDocumentIntelligenceService docIntel,
        BackgroundJobTestHelpers.CapturingJobQueueService queue) =>
        new(
            NullLogger<ExtractNotebookFileMarkdownHandler>.Instance,
            docIntel,
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            BackgroundJobTestHelpers.CreateFactory(options),
            queue,
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));
}
