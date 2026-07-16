using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.BackgroundJobs.Scheduling;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Scheduling;

[TestClass]
public sealed class ProjectScheduledJobInFlightGuardTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];
    [TestMethod]
    public async Task FailOrphanedRunningRunsAsync_MarksRunsWithoutProcessingQueueItemAsFailed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"orphan-runs-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.ProjectScheduledJobRuns.Add(new ProjectScheduledJobRun
            {
                ScheduledJobId = jobId,
                TriggeredBy = ProjectScheduledJobTrigger.Schedule,
                Status = ProjectScheduledJobRunStatus.Running,
                StartedUtc = DateTime.UtcNow.AddMinutes(-2)
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            await ProjectScheduledJobInFlightGuard.FailOrphanedRunningRunsAsync(db, CancellationToken.None);
        }

        await using var verify = new ApplicationDbContext(options);
        var run = await verify.ProjectScheduledJobRuns.SingleAsync();
        run.Status.Should().Be(ProjectScheduledJobRunStatus.Failed);
        run.ErrorMessage.Should().Contain("process restart");
    }

    [TestMethod]
    public async Task FailOrphanedRunningRunsAsync_LeavesRunAloneWhenProcessingQueueItemExists()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"live-run-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.ProjectScheduledJobRuns.Add(new ProjectScheduledJobRun
            {
                ScheduledJobId = jobId,
                TriggeredBy = ProjectScheduledJobTrigger.Schedule,
                Status = ProjectScheduledJobRunStatus.Running,
                StartedUtc = DateTime.UtcNow
            });
            db.JobQueue.Add(new JobQueue
            {
                JobType = ProjectScheduledJobExecutionJob.JobType,
                PayloadJson = JsonSerializer.Serialize(new ProjectScheduledJobExecutionJob(jobId, "Schedule")),
                Status = JobStatus.Processing,
                ClaimToken = Guid.NewGuid(),
                RowVersion = DefaultRowVersion
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            await ProjectScheduledJobInFlightGuard.FailOrphanedRunningRunsAsync(db, CancellationToken.None);
        }

        await using var verify = new ApplicationDbContext(options);
        var run = await verify.ProjectScheduledJobRuns.SingleAsync();
        run.Status.Should().Be(ProjectScheduledJobRunStatus.Running);
    }

    [TestMethod]
    public async Task ReconcileBeforeExecutionAsync_SkipsWhenRunAndProcessingQueueItemExist()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"skip-dup-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.ProjectScheduledJobRuns.Add(new ProjectScheduledJobRun
            {
                ScheduledJobId = jobId,
                TriggeredBy = ProjectScheduledJobTrigger.Schedule,
                Status = ProjectScheduledJobRunStatus.Running,
                StartedUtc = DateTime.UtcNow
            });
            db.JobQueue.Add(new JobQueue
            {
                JobType = ProjectScheduledJobExecutionJob.JobType,
                PayloadJson = JsonSerializer.Serialize(new ProjectScheduledJobExecutionJob(jobId, "Schedule")),
                Status = JobStatus.Processing,
                ClaimToken = Guid.NewGuid(),
                RowVersion = DefaultRowVersion
            });
            await db.SaveChangesAsync();
        }

        await using var reconcileDb = new ApplicationDbContext(options);
        var gate = await ProjectScheduledJobInFlightGuard.ReconcileBeforeExecutionAsync(
            reconcileDb,
            jobId,
            CancellationToken.None);

        gate.Should().Be(ProjectScheduledJobExecutionGate.SkipDuplicate);
    }

    [TestMethod]
    public async Task HasInFlightQueueItemAsync_ReturnsTrueForPendingScheduledJobPayload()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pending-queue-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.JobQueue.Add(new JobQueue
            {
                JobType = ProjectScheduledJobExecutionJob.JobType,
                PayloadJson = JsonSerializer.Serialize(new ProjectScheduledJobExecutionJob(jobId, "Schedule")),
                Status = JobStatus.Pending,
                RowVersion = DefaultRowVersion
            });
            await db.SaveChangesAsync();
        }

        await using var verify = new ApplicationDbContext(options);
        var hasInFlight = await ProjectScheduledJobInFlightGuard.HasInFlightQueueItemAsync(
            verify,
            jobId,
            CancellationToken.None);

        hasInFlight.Should().BeTrue();
    }
}
