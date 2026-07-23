using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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

        Guid projectId;
        Guid notebookId;
        try
        {
            await using (var seed = new ApplicationDbContext(options))
            {
                (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
                var notebookRoot = Path.Combine(storageRoot, "test-project", "test-notebook");
                Directory.CreateDirectory(notebookRoot);
                await File.WriteAllTextAsync(Path.Combine(notebookRoot, "notes.md"), "# hello");
            }

            var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();
            var notebookRootPath = Path.Combine(storageRoot, "test-project", "test-notebook");
            var reconciler = CreateReconciler(options, projectId, notebookId, notebookRootPath, queue);
            var handler = new SyncNotebookHandler(
                NullLogger<SyncNotebookHandler>.Instance,
                reconciler);

            var result = await handler.HandleAsync(new SyncNotebookJob(notebookId), CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
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
            var reconciler = CreateReconciler(
                options,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Path.Combine(storageRoot, "test-project", "test-notebook"),
                new BackgroundJobTestHelpers.CapturingJobQueueService());
            var handler = new SyncNotebookHandler(
                NullLogger<SyncNotebookHandler>.Instance,
                reconciler);

            var result = await handler.HandleAsync(new SyncNotebookJob(Guid.NewGuid()), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static NotebookFileReconciler CreateReconciler(
        DbContextOptions<ApplicationDbContext> options,
        Guid projectId,
        Guid notebookId,
        string notebookRoot,
        BackgroundJobTestHelpers.CapturingJobQueueService queue)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext)))
            .Returns(() => new ApplicationDbContext(options));

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

        return new NotebookFileReconciler(
            scopeFactoryMock.Object,
            pathResolver.Object,
            queue,
            Mock.Of<IFileLineageService>(),
            Mock.Of<IUsageRecorder>(),
            new InMemoryNotebookLockService(),
            NullLogger<NotebookFileReconciler>.Instance);
    }
}
