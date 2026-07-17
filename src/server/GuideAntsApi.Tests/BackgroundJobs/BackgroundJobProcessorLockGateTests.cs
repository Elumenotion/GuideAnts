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

public sealed class BackgroundJobProcessorLockGateTests

{

    [TestInitialize]

    public void ResetCounts() => RecordingJobHandler.HandleCounts.Clear();



    [TestMethod]

    public async Task ActiveConversationLock_BlocksGatedIndexDirectTextFileClaim_WhenBothLocalAi()

    {

        var fixture = await CreateFixtureAsync(bothUseLocalAi: true);

        var jobId = await EnqueuePendingJobAsync(fixture, "IndexDirectTextFile");

        await SeedActiveConversationLockAsync(fixture.Options, expiresAt: DateTime.UtcNow.AddMinutes(5));



        await RunSinglePollAsync(fixture.Processor);



        await using var verify = new ApplicationDbContext(fixture.Options);

        var job = await verify.JobQueue.SingleAsync(j => j.Id == jobId);

        job.Status.Should().Be(JobStatus.Pending);

        RecordingJobHandler.HandleCounts.GetValueOrDefault("IndexDirectTextFile").Should().Be(0);

    }



    [TestMethod]

    public async Task ActiveConversationLock_BlocksGatedExtractNotebookFileMarkdownClaim_WhenBothLocalAi()

    {

        var fixture = await CreateFixtureAsync(bothUseLocalAi: true);

        var jobId = await EnqueuePendingJobAsync(fixture, "ExtractNotebookFileMarkdown");

        await SeedActiveConversationLockAsync(fixture.Options, expiresAt: DateTime.UtcNow.AddMinutes(5));



        await RunSinglePollAsync(fixture.Processor);



        await using var verify = new ApplicationDbContext(fixture.Options);

        (await verify.JobQueue.SingleAsync(j => j.Id == jobId)).Status.Should().Be(JobStatus.Pending);

        RecordingJobHandler.HandleCounts.GetValueOrDefault("ExtractNotebookFileMarkdown").Should().Be(0);

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



    private static Task<LockGateFixture> CreateFixtureAsync(bool bothUseLocalAi)

    {

        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"lock-gate-{Guid.NewGuid():N}");

        var services = new ServiceCollection();

        services.AddLogging();

        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(BackgroundJobTestHelpers.CreateFactory(options));

        services.AddSingleton<IActiveJobExecutionRegistry, ActiveJobExecutionRegistry>();

        services.AddSingleton<IJobQueueService, JobQueueService>();

        services.AddSingleton<IJobHandler>(new RecordingJobHandler("IndexDirectTextFile"));

        services.AddSingleton<IJobHandler>(new RecordingJobHandler("ExtractNotebookFileMarkdown"));

        services.AddSingleton<IJobHandler>(new RecordingJobHandler("Test"));

        services.AddSingleton<IConversationLockGateEligibility>(new StubConversationLockGateEligibility(bothUseLocalAi));

        services.AddSingleton(OptionsFactory.Create(new JobRetryOptions()));

        services.AddSingleton(OptionsFactory.Create(new JobProcessorOptions

        {

            JobTypes = new Dictionary<string, JobTypeOptions>

            {

                ["IndexDirectTextFile"] = new() { MaxConcurrency = 1, LeaseSeconds = 30 },

                ["ExtractNotebookFileMarkdown"] = new() { MaxConcurrency = 1, LeaseSeconds = 30 },

                ["Test"] = new() { MaxConcurrency = 1, LeaseSeconds = 30 },

            },

            ConversationLockGate = new ConversationLockGateOptions

            {

                Enabled = true,

                GatedJobTypes = new HashSet<string>(StringComparer.Ordinal)

                {

                    "IndexDirectTextFile",

                    "ExtractNotebookFileMarkdown",

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



        return Task.FromResult(new LockGateFixture(options, processor));

    }



    private static async Task<Guid> EnqueuePendingJobAsync(LockGateFixture fixture, string jobType)

    {

        var jobId = Guid.NewGuid();

        await using var context = new ApplicationDbContext(fixture.Options);

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



    private static async Task SeedActiveConversationLockAsync(DbContextOptions<ApplicationDbContext> options, DateTime expiresAt)

    {

        await using var context = new ApplicationDbContext(options);

        var projectId = Guid.NewGuid();

        var notebookId = Guid.NewGuid();

        var conversationId = Guid.NewGuid();



        context.Projects.Add(new Project

        {

            Id = projectId,

            Title = "Lock Gate Project",

            Slug = "lock-gate-project",

            Created = DateTime.UtcNow,

        });

        context.Notebooks.Add(new Notebook

        {

            Id = notebookId,

            ProjectId = projectId,

            Title = "Lock Gate Notebook",

            Slug = "lock-gate-notebook",

            Created = DateTime.UtcNow,

        });

        context.NotebookConversations.Add(new NotebookConversation

        {

            Id = conversationId,

            NotebookId = notebookId,

            Title = "Locked conversation",

            Created = DateTime.UtcNow,

        });

        context.ConversationLocks.Add(new ConversationLock

        {

            ConversationId = conversationId,

            LockedByUserName = "tester",

            LockedAt = DateTime.UtcNow.AddMinutes(-1),

            ExpiresAt = expiresAt,

        });

        await context.SaveChangesAsync();

    }



    private sealed record LockGateFixture(

        DbContextOptions<ApplicationDbContext> Options,

        BackgroundJobProcessor Processor);



    private sealed class StubConversationLockGateEligibility(bool bothUseLocalAi) : IConversationLockGateEligibility

    {

        public Task<bool> BothUseLocalAiAsync(CancellationToken cancellationToken = default) =>

            Task.FromResult(bothUseLocalAi);

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

