using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class SyncNotebookHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Adds_new_physical_files_and_enqueues_index_job()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-notebook-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(Path.GetTempPath(), $"sync-notebook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storageRoot);

        Guid notebookId;
        try
        {
            await using (var seed = new ApplicationDbContext(options))
            {
                var (projectId, nbId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
                notebookId = nbId;
                var notebookRoot = Path.Combine(storageRoot, "test-project", "test-notebook");
                Directory.CreateDirectory(notebookRoot);
                await File.WriteAllTextAsync(Path.Combine(notebookRoot, "notes.md"), "# hello");
            }

            var factory = BackgroundJobTestHelpers.CreateFactory(options);
            var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();
            var handler = new SyncNotebookHandler(
                NullLogger<SyncNotebookHandler>.Instance,
                factory,
                BackgroundJobTestHelpers.CreateConfiguration(storageRoot),
                queue);

            var success = await handler.HandleAsync(new SyncNotebookJob(notebookId), CancellationToken.None);

            success.Should().BeTrue();
            await using var verify = new ApplicationDbContext(options);
            var files = await verify.NotebookFiles.Where(f => f.NotebookId == notebookId).ToListAsync();
            files.Should().ContainSingle(f => f.RelativePath == "notes.md");
            queue.Enqueued.Should().ContainSingle(e => e.JobType == "IndexDirectTextFile");
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task HandleAsync_Returns_true_when_notebook_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-missing-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(Path.GetTempPath(), $"sync-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storageRoot);
        try
        {
            var handler = new SyncNotebookHandler(
                NullLogger<SyncNotebookHandler>.Instance,
                BackgroundJobTestHelpers.CreateFactory(options),
                BackgroundJobTestHelpers.CreateConfiguration(storageRoot),
                new BackgroundJobTestHelpers.CapturingJobQueueService());

            var success = await handler.HandleAsync(new SyncNotebookJob(Guid.NewGuid()), CancellationToken.None);
            success.Should().BeTrue();
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
