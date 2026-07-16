using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Scheduling;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class JobQueueServiceScheduledJobReconciliationTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];

    [TestMethod]
    public async Task FailOrphanedRunningRunsAsync_AfterQueueMarkedFailedOnRestart_MarksRunFailed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"requeue-scheduled-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        Guid queueItemId;

        await using (var db = new ApplicationDbContext(options))
        {
            db.ProjectScheduledJobRuns.Add(new ProjectScheduledJobRun
            {
                ScheduledJobId = jobId,
                TriggeredBy = ProjectScheduledJobTrigger.Manual,
                Status = ProjectScheduledJobRunStatus.Running,
                StartedUtc = DateTime.UtcNow
            });
            var queueItem = new JobQueue
            {
                JobType = ProjectScheduledJobExecutionJob.JobType,
                PayloadJson = JsonSerializer.Serialize(new ProjectScheduledJobExecutionJob(jobId, "Manual")),
                Status = JobStatus.Processing,
                ClaimToken = Guid.NewGuid(),
                RowVersion = DefaultRowVersion
            };
            db.JobQueue.Add(queueItem);
            await db.SaveChangesAsync();
            queueItemId = queueItem.Id;
        }

        // Scheduler startup can run before the job processor requeues Processing rows.
        await using (var schedulerDb = new ApplicationDbContext(options))
        {
            await ProjectScheduledJobInFlightGuard.FailOrphanedRunningRunsAsync(schedulerDb, CancellationToken.None);
        }

        await using (var midVerify = new ApplicationDbContext(options))
        {
            (await midVerify.ProjectScheduledJobRuns.SingleAsync()).Status
                .Should().Be(ProjectScheduledJobRunStatus.Running);
        }

        // Job processor startup marks interrupted scheduled queue items Failed, then reconciles runs.
        await using (var processorDb = new ApplicationDbContext(options))
        {
            var queueItem = await processorDb.JobQueue.SingleAsync(q => q.Id == queueItemId);
            queueItem.Status = JobStatus.Failed;
            queueItem.ClaimToken = Guid.Empty;
            queueItem.LeaseUntil = null;
            queueItem.ErrorMessage = "Interrupted by process restart; scheduled jobs are not automatically retried.";
            queueItem.UpdatedUtc = DateTime.UtcNow;
            await processorDb.SaveChangesAsync();

            await ProjectScheduledJobInFlightGuard.FailOrphanedRunningRunsAsync(processorDb, CancellationToken.None);
        }

        await using var verify = new ApplicationDbContext(options);
        var run = await verify.ProjectScheduledJobRuns.SingleAsync();
        run.Status.Should().Be(ProjectScheduledJobRunStatus.Failed);
        run.ErrorMessage.Should().Contain("process restart");
    }
}
