using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class IndexDirectTextFileHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Returns_false_when_notebook_file_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"index-direct-missing-{Guid.NewGuid():N}");
        var indexer = new Mock<IHybridIndexer>();
        var handler = new IndexDirectTextFileHandler(
            NullLogger<IndexDirectTextFileHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()),
            indexer.Object);

        var success = await handler.HandleAsync(new IndexDirectTextFileJob(Guid.NewGuid(), IsContentFile: false), CancellationToken.None);

        success.Should().BeFalse();
        indexer.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task HandleAsync_Indexes_notebook_text_file_when_present_on_disk()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"index-direct-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(Path.GetTempPath(), $"index-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storageRoot);
        Guid notebookFileId;

        try
        {
            await using (var seed = new ApplicationDbContext(options))
            {
                var (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
                var notebookRoot = Path.Combine(storageRoot, "test-project", "test-notebook");
                Directory.CreateDirectory(notebookRoot);
                await File.WriteAllTextAsync(Path.Combine(notebookRoot, "notes.md"), "content");

                var notebookFile = new NotebookFile
                {
                    NotebookId = notebookId,
                    RelativePath = "notes.md",
                    FileSize = 7,
                    LastModifiedUtc = DateTime.UtcNow,
                    FileHash = "hash"
                };
                notebookFile.GenerateDocumentId(notebookId);
                seed.NotebookFiles.Add(notebookFile);
                await seed.SaveChangesAsync();
                notebookFileId = notebookFile.Id;
            }

            var indexer = new Mock<IHybridIndexer>();
            var handler = new IndexDirectTextFileHandler(
                NullLogger<IndexDirectTextFileHandler>.Instance,
                BackgroundJobTestHelpers.CreateFactory(options),
                BackgroundJobTestHelpers.CreateConfiguration(storageRoot),
                indexer.Object);

            var success = await handler.HandleAsync(new IndexDirectTextFileJob(notebookFileId, IsContentFile: false), CancellationToken.None);

            success.Should().BeTrue();
            indexer.Verify(
                x => x.IndexNotebookFileAsync(
                    notebookFileId,
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.Is<string>(p => p.EndsWith("notes.md")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }
}
