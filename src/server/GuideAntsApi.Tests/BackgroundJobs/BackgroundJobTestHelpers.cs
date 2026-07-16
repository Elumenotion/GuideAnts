using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.BackgroundJobs;

internal static class BackgroundJobTestHelpers
{
    internal static DbContextOptions<ApplicationDbContext> CreateInMemoryOptions(string name) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    internal static IConfiguration CreateConfiguration(string storageRoot, bool retentionEnabled = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = storageRoot,
                ["BackgroundJobs:JobTypes:RetentionCleanup:Enabled"] = retentionEnabled ? "true" : "false"
            })
            .Build();

    internal static async Task<(Guid ProjectId, Guid NotebookId)> SeedProjectNotebookAsync(
        ApplicationDbContext context,
        string projectSlug = "test-project",
        string notebookSlug = "test-notebook")
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        context.Projects.Add(new Project
        {
            Id = projectId,
            Title = "Test Project",
            Slug = projectSlug,
            Created = DateTime.UtcNow
        });
        context.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            ProjectId = projectId,
            Title = "Test Notebook",
            Slug = notebookSlug,
            Created = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return (projectId, notebookId);
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

        public Task<bool> FailAsync(Guid id, Guid claimToken, string error, JobFailureClass failureClass = JobFailureClass.RetryableTransient, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> RequeueExpiredAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<int> RequeueAllProcessingAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    internal static TestDbContextFactory CreateFactory(DbContextOptions<ApplicationDbContext> options) =>
        new(options);
}
