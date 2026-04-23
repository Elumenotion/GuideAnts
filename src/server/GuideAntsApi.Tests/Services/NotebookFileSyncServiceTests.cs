using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookFileSyncServiceTests
{
    [TestMethod]
    public async Task QueueNotebookSyncAsync_EnqueuesSyncNotebookJob()
    {
        var queue = new CapturingJobQueueService();
        var services = new ServiceCollection();
        services.AddSingleton<IJobQueueService>(queue);

        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = Path.GetTempPath()
            })
            .Build();

        var service = new NotebookFileSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<NotebookFileSyncService>.Instance,
            Mock.Of<IFileLineageService>(),
            Mock.Of<IMarkdownExtractionService>(),
            Mock.Of<IUsageRecorder>(),
            Mock.Of<INotebookLockService>());

        var notebookId = Guid.NewGuid();

        await service.QueueNotebookSyncAsync(notebookId);

        queue.LastJobType.Should().Be("SyncNotebook");
        queue.LastPayload.Should().BeOfType<SyncNotebookJob>()
            .Which.NotebookId.Should().Be(notebookId);
    }

    private sealed class CapturingJobQueueService : IJobQueueService
    {
        public string? LastJobType { get; private set; }
        public object? LastPayload { get; private set; }

        public Task<Guid> EnqueueAsync(string jobType, object payload, int priority = 0, DateTime? availableAt = null, Guid? correlationId = null, int? maxAttempts = null, CancellationToken ct = default)
        {
            LastJobType = jobType;
            LastPayload = payload;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<JobQueue?> TryClaimAsync(string? jobType, int leaseSeconds, CancellationToken ct = default)
            => Task.FromResult<JobQueue?>(null);

        public Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> FailAsync(Guid id, Guid claimToken, string error, int baseDelaySeconds = 10, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<int> RequeueExpiredAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RequeueAllProcessingAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
