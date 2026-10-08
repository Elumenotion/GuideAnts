using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class BackgroundJobProcessorEmbeddingsGateTests
{
    [TestInitialize]
    public void ResetCounts() => RecordingJobHandler.HandleCounts.Clear();

    [TestMethod]
    public async Task UnloadedEmbeddings_DefersGatedIndexJobClaim()
    {
        var fixture = await CreateFixtureAsync(loaded: false, usesLocal: true);
        var jobId = await EnqueuePendingJobAsync(fixture.Options, "IndexNotebookMarkdownShadow");

        await RunSinglePollAsync(fixture.Processor);

        await using var verify = new ApplicationDbContext(fixture.Options);
        var job = await verify.JobQueue.SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(JobStatus.Pending);
        RecordingJobHandler.HandleCounts.GetValueOrDefault("IndexNotebookMarkdownShadow").Should().Be(0);
    }

    private static async Task RunSinglePollAsync(BackgroundJobProcessor processor)
    {
        var initialize = typeof(BackgroundJobProcessor).GetMethod(
            "InitializeJobHandlersAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        await (Task)initialize!.Invoke(processor, null)!;

        var process = typeof(BackgroundJobProcessor).GetMethod(
            "ProcessAvailableJobsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        await (Task)process!.Invoke(processor, [CancellationToken.None])!;
        await Task.Delay(750);
    }

    private static Task<EmbeddingsGateFixture> CreateFixtureAsync(
        bool loaded,
        bool usesLocal)
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"emb-gate-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(BackgroundJobTestHelpers.CreateFactory(options));
        services.AddSingleton<IActiveJobExecutionRegistry, ActiveJobExecutionRegistry>();
        services.AddSingleton<IJobQueueService, JobQueueService>();
        services.AddSingleton<IJobHandler>(new RecordingJobHandler("IndexNotebookMarkdownShadow"));
        services.AddSingleton<IEmbeddingsReadinessGate>(new StubEmbeddingsReadinessGate(usesLocal, loaded));

        services.AddSingleton(OptionsFactory.Create(new JobRetryOptions()));
        services.AddSingleton(OptionsFactory.Create(new JobProcessorOptions
        {
            JobTypes = new Dictionary<string, JobTypeOptions>
            {
                ["IndexNotebookMarkdownShadow"] = new() { MaxConcurrency = 1, LeaseSeconds = 30 },
            },
            EmbeddingsGate = new EmbeddingsJobGateOptions
            {
                Enabled = true,
                GatedJobTypes = new HashSet<string>(StringComparer.Ordinal)
                {
                    "IndexNotebookMarkdownShadow",
                }
            }
        }));

        var provider = services.BuildServiceProvider();
        var processor = new BackgroundJobProcessor(
            provider,
            provider.GetRequiredService<IOptions<JobProcessorOptions>>(),
            provider.GetRequiredService<IOptions<JobRetryOptions>>(),
            provider.GetRequiredService<IActiveJobExecutionRegistry>(),
            NullLogger<BackgroundJobProcessor>.Instance);

        return Task.FromResult(new EmbeddingsGateFixture(options, processor));
    }

    private static async Task<Guid> EnqueuePendingJobAsync(DbContextOptions<ApplicationDbContext> options, string jobType)
    {
        var jobId = Guid.NewGuid();
        await using var context = new ApplicationDbContext(options);
        context.JobQueue.Add(new JobQueue
        {
            Id = jobId,
            JobType = jobType,
            PayloadJson = "{}",
            Status = JobStatus.Pending,
            AvailableAt = DateTime.UtcNow,
            ClaimToken = Guid.Empty,
            MaxAttempts = 40,
            Created = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            RowVersion = [1],
        });
        await context.SaveChangesAsync();
        return jobId;
    }

    private sealed record EmbeddingsGateFixture(
        DbContextOptions<ApplicationDbContext> Options,
        BackgroundJobProcessor Processor);

    private sealed class StubEmbeddingsReadinessGate(bool usesLocal, bool loaded) : IEmbeddingsReadinessGate
    {
        public Task<bool> UsesLocalEmbeddingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(usesLocal);
        }

        public Task<(bool Loaded, string? Error)> EnsureLocalEmbeddingsLoadedAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult((loaded, loaded ? null : "engine not loaded"));
        }
    }

    private sealed class RecordingJobHandler(string jobType) : IJobHandler
    {
        public static ConcurrentDictionary<string, int> HandleCounts { get; } = new(StringComparer.Ordinal);

        public string JobType { get; } = jobType;

        public Task<JobExecutionResult> HandleAsync(string payloadJson, CancellationToken cancellationToken)
        {
            HandleCounts.AddOrUpdate(JobType, 1, static (_, count) => count + 1);
            return Task.FromResult(JobExecutionResult.Success());
        }
    }
}
