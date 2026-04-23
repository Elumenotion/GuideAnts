using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class EmbeddingsRebuildServiceTests
{
    [TestMethod]
    public async Task RequestRebuildAsync_Enqueues_WhenNoPendingOrProcessingJobExists()
    {
        var options = CreateInMemoryOptions();
        var factory = new TestDbContextFactory(options);
        var fakeQueue = new CapturingJobQueueService { NextJobId = Guid.NewGuid() };
        var service = new EmbeddingsRebuildService(factory, fakeQueue);

        var result = await service.RequestRebuildAsync();

        result.Enqueued.Should().BeTrue();
        result.JobId.Should().Be(fakeQueue.NextJobId);
        result.Status.Should().Be("queued");
        fakeQueue.EnqueuedJobTypes.Should().ContainSingle()
            .Which.Should().Be(nameof(RebuildEmbeddingsJob).Replace("Job", string.Empty));
    }

    [TestMethod]
    public async Task RequestRebuildAsync_ReturnsConflict_WhenPendingJobExists()
    {
        var options = CreateInMemoryOptions();
        await using (var seedContext = new ApplicationDbContext(options))
        {
            seedContext.JobQueue.Add(new JobQueue
            {
                Id = Guid.NewGuid(),
                JobType = nameof(RebuildEmbeddingsJob).Replace("Job", string.Empty),
                PayloadJson = "{}",
                Status = JobStatus.Pending,
                AvailableAt = DateTime.UtcNow,
                ClaimToken = Guid.Empty,
                Created = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                RowVersion = [1]
            });
            await seedContext.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(options);
        var fakeQueue = new CapturingJobQueueService();
        var service = new EmbeddingsRebuildService(factory, fakeQueue);

        var result = await service.RequestRebuildAsync();

        result.Enqueued.Should().BeFalse();
        result.Status.Should().Be("pending");
        fakeQueue.EnqueuedJobTypes.Should().BeEmpty();
    }

    private static DbContextOptions<ApplicationDbContext> CreateInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"emb-rebuild-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private sealed class CapturingJobQueueService : IJobQueueService
    {
        public Guid NextJobId { get; set; } = Guid.NewGuid();
        public List<string> EnqueuedJobTypes { get; } = [];

        public Task<Guid> EnqueueAsync(
            string jobType,
            object payload,
            int priority = 0,
            DateTime? availableAt = null,
            Guid? correlationId = null,
            int? maxAttempts = null,
            CancellationToken ct = default)
        {
            EnqueuedJobTypes.Add(jobType);
            return Task.FromResult(NextJobId);
        }

        public Task<JobQueue?> TryClaimAsync(string? jobType, int leaseSeconds, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> FailAsync(Guid id, Guid claimToken, string error, int baseDelaySeconds = 10, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> RequeueExpiredAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> RequeueAllProcessingAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
