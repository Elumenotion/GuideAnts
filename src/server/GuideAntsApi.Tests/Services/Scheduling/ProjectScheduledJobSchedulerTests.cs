using System.Reflection;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Scheduling;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace GuideAntsApi.Tests.Services.Scheduling;

[TestClass]
public sealed class ProjectScheduledJobSchedulerTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];

    private static ProjectScheduledJob CreateScheduledJob(
        Guid jobId,
        Guid projectId,
        Guid notebookId,
        DateTime? nextRunUtc = null) =>
        new()
        {
            Id = jobId,
            ProjectId = projectId,
            NotebookId = notebookId,
            Name = "Due job",
            JobType = ProjectScheduledJobType.NewConversation,
            IsEnabled = true,
            CronExpression = "0 9 * * *",
            TimeZoneId = "UTC",
            Prompt = "hello",
            AssistantName = "assistant",
            CreatedByUserId = Guid.NewGuid(),
            NextRunUtc = nextRunUtc,
            RowVersion = DefaultRowVersion
        };
    [TestMethod]
    public async Task RecomputeNextRunsAsync_LeavesPastDueNextRunForCatchUp()
    {
        var pastDue = DateTime.UtcNow.AddHours(-2);
        var (scopeFactory, jobId) = await CreateSchedulerScopeAsync(pastDue);

        await InvokePrivateAsync(scopeFactory, "RecomputeNextRunsAsync", CancellationToken.None);

        await using var db = await scopeFactory.CreateScope().ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync();
        var job = await db.ProjectScheduledJobs.SingleAsync(j => j.Id == jobId);
        job.NextRunUtc.Should().BeCloseTo(pastDue, TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task ProcessDueJobsAsync_SkipsWhenRunAlreadyInProgress()
    {
        var pastDue = DateTime.UtcNow.AddMinutes(-5);
        var (scopeFactory, jobId) = await CreateSchedulerScopeAsync(pastDue, addRunningRun: true);
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<CapturingJobQueueService>();

        await InvokePrivateAsync(scopeFactory, "ProcessDueJobsAsync", CancellationToken.None);

        queue.Enqueued.Should().BeEmpty();

        await using var db = await scopeFactory.CreateScope().ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync();
        var job = await db.ProjectScheduledJobs.SingleAsync(j => j.Id == jobId);
        job.NextRunUtc.Should().BeCloseTo(pastDue, TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task ProcessDueJobsAsync_EnqueuesCatchUpRunWhenDueAndNotRunning()
    {
        var pastDue = DateTime.UtcNow.AddMinutes(-5);
        var (scopeFactory, jobId) = await CreateSchedulerScopeAsync(pastDue);
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<CapturingJobQueueService>();

        await InvokePrivateAsync(scopeFactory, "ProcessDueJobsAsync", CancellationToken.None);

        queue.Enqueued.Should().ContainSingle();
        queue.Enqueued[0].JobType.Should().Be(ProjectScheduledJobExecutionJob.JobType);
        var payload = queue.Enqueued[0].Payload.Should().BeOfType<ProjectScheduledJobExecutionJob>().Subject;
        payload.ScheduledJobId.Should().Be(jobId);
    }

    [TestMethod]
    public async Task ProcessDueJobsAsync_SkipsWhenQueueItemAlreadyPending()
    {
        var pastDue = DateTime.UtcNow.AddMinutes(-5);
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"queue-skip-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = $"proj-{Guid.NewGuid():N}" });
            db.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = $"nb-{Guid.NewGuid():N}"
            });
            db.ProjectScheduledJobs.Add(CreateScheduledJob(jobId, projectId, notebookId, pastDue));
            db.JobQueue.Add(new JobQueue
            {
                JobType = ProjectScheduledJobExecutionJob.JobType,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new ProjectScheduledJobExecutionJob(jobId, ProjectScheduledJobTrigger.Schedule.ToString())),
                Status = JobStatus.Pending,
                RowVersion = DefaultRowVersion
            });
            await db.SaveChangesAsync();
        }

        var queue = new CapturingJobQueueService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new TestDbContextFactory(options));
        services.AddSingleton<IJobQueueService>(queue);
        services.AddSingleton<ICronScheduleService, CronScheduleService>();
        services.AddSingleton<IOptions<ProjectScheduledJobOptions>>(OptionsFactory.Create(new ProjectScheduledJobOptions
        {
            Enabled = true,
            PollIntervalSeconds = 15
        }));
        services.AddSingleton<ProjectScheduledJobScheduler>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await InvokePrivateAsync(scopeFactory, "ProcessDueJobsAsync", CancellationToken.None);

        queue.Enqueued.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EnqueueManualRunAsync_ThrowsWhenRunAlreadyInProgress()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"manual-overlap-{Guid.NewGuid():N}");
        await using (var db = new ApplicationDbContext(options))
        {
            var projectId = Guid.NewGuid();
            var notebookId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            db.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = $"proj-{Guid.NewGuid():N}" });
            db.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = $"nb-{Guid.NewGuid():N}"
            });
            db.ProjectScheduledJobs.Add(new ProjectScheduledJob
            {
                Id = jobId,
                ProjectId = projectId,
                NotebookId = notebookId,
                Name = "Overlap test",
                JobType = ProjectScheduledJobType.NewConversation,
                CronExpression = "0 9 * * *",
                TimeZoneId = "UTC",
                Prompt = "hello",
                AssistantName = "assistant",
                CreatedByUserId = Guid.NewGuid(),
                RowVersion = DefaultRowVersion
            });
            db.ProjectScheduledJobRuns.Add(new ProjectScheduledJobRun
            {
                ScheduledJobId = jobId,
                TriggeredBy = ProjectScheduledJobTrigger.Manual,
                Status = ProjectScheduledJobRunStatus.Running,
                StartedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new ProjectScheduledJobService(
                new TestDbContextFactory(options),
                new ScheduleBuilderService(),
                new CronScheduleService(),
                new CapturingJobQueueService());

            var act = () => service.EnqueueManualRunAsync(projectId, jobId);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already in progress*");
        }
    }

    private static async Task<(IServiceScopeFactory ScopeFactory, Guid JobId)> CreateSchedulerScopeAsync(
        DateTime nextRunUtc,
        bool addRunningRun = false)
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"scheduler-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = $"proj-{Guid.NewGuid():N}" });
            db.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = $"nb-{Guid.NewGuid():N}"
            });
            db.ProjectScheduledJobs.Add(CreateScheduledJob(jobId, projectId, notebookId, nextRunUtc));

            if (addRunningRun)
            {
                db.ProjectScheduledJobRuns.Add(new ProjectScheduledJobRun
                {
                    ScheduledJobId = jobId,
                    TriggeredBy = ProjectScheduledJobTrigger.Schedule,
                    Status = ProjectScheduledJobRunStatus.Running,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-1)
                });
            }

            await db.SaveChangesAsync();
        }

        var queue = new CapturingJobQueueService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new TestDbContextFactory(options));
        services.AddSingleton<IJobQueueService>(queue);
        services.AddSingleton<ICronScheduleService, CronScheduleService>();
        services.AddSingleton<IOptions<ProjectScheduledJobOptions>>(OptionsFactory.Create(new ProjectScheduledJobOptions
        {
            Enabled = true,
            PollIntervalSeconds = 15
        }));
        services.AddSingleton<ProjectScheduledJobScheduler>();
        services.AddSingleton(queue);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IServiceScopeFactory>(), jobId);
    }

    private static async Task InvokePrivateAsync(IServiceScopeFactory scopeFactory, string methodName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<ProjectScheduledJobScheduler>();
        var method = typeof(ProjectScheduledJobScheduler).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        await (Task)method!.Invoke(scheduler, [ct])!;
    }

    internal sealed class CapturingJobQueueService : IJobQueueService
    {
        public List<(string JobType, object Payload)> Enqueued { get; } = [];

        public Task<Guid> EnqueueAsync(
            string jobType,
            object payload,
            int priority = 0,
            DateTime? availableAt = null,
            Guid? correlationId = null,
            int? maxAttempts = null,
            CancellationToken ct = default)
        {
            Enqueued.Add((jobType, payload));
            return Task.FromResult(Guid.NewGuid());
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
