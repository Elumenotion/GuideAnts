using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookFileSyncServiceDedupeTests
{
    [TestMethod]
    public async Task QueueReconcileAsync_SkipsWhenPendingSyncNotebookExistsForNotebook()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-dedupe-{Guid.NewGuid():N}");
        var notebookId = Guid.NewGuid();
        var queue = new RecordingJobQueueService();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.JobQueue.Add(new JobQueue
            {
                JobType = "SyncNotebook",
                PayloadJson = "{}",
                Status = JobStatus.Pending,
                CorrelationId = notebookId,
                RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
            });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton<IJobQueueService>(queue);
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            BackgroundJobTestHelpers.CreateFactory(options));

        await using var provider = services.BuildServiceProvider();
        var service = new NotebookFileSyncService(
            Mock.Of<INotebookFileReconciler>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotebookFileSyncService>.Instance);

        await service.QueueReconcileAsync(notebookId);

        queue.EnqueueCount.Should().Be(0);
    }

    [TestMethod]
    public async Task QueueReconcileAsync_EnqueuesWithCorrelationIdWhenNotAlreadyQueued()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-enqueue-{Guid.NewGuid():N}");
        var notebookId = Guid.NewGuid();
        var queue = new RecordingJobQueueService();

        var services = new ServiceCollection();
        services.AddSingleton<IJobQueueService>(queue);
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
            BackgroundJobTestHelpers.CreateFactory(options));

        await using var provider = services.BuildServiceProvider();
        var service = new NotebookFileSyncService(
            Mock.Of<INotebookFileReconciler>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotebookFileSyncService>.Instance);

        await service.QueueReconcileAsync(notebookId);

        queue.EnqueueCount.Should().Be(1);
        queue.LastCorrelationId.Should().Be(notebookId);
        queue.LastJobType.Should().Be("SyncNotebook");
    }

    private sealed class RecordingJobQueueService : IJobQueueService
    {
        public int EnqueueCount { get; private set; }
        public string? LastJobType { get; private set; }
        public Guid? LastCorrelationId { get; private set; }

        public Task<Guid> EnqueueAsync(
            string jobType,
            object payload,
            int priority = 0,
            DateTime? availableAt = null,
            Guid? correlationId = null,
            int? maxAttempts = null,
            CancellationToken ct = default)
        {
            EnqueueCount++;
            LastJobType = jobType;
            LastCorrelationId = correlationId;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<JobQueue?> TryClaimAsync(string? jobType, int leaseSeconds, CancellationToken ct = default)
            => Task.FromResult<JobQueue?>(null);

        public Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> FailAsync(Guid id, Guid claimToken, string error, JobFailureClass failureClass = JobFailureClass.RetryableTransient, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<int> RequeueExpiredAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RequeueAllProcessingAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
