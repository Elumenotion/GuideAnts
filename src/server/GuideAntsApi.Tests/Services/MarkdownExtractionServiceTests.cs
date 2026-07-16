using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class MarkdownExtractionServiceTests
{
    [TestMethod]
    public async Task RetryNotebookExtractionAsync_ExistingShadow_ResetsStatusAndQueuesExtraction()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"markdown-retry-{Guid.NewGuid():N}", databaseRoot)
            .Options;
        var scopeFactory = new DbContextOptionsScopeFactory(dbOptions);
        var queue = new CapturingJobQueueService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = Path.GetTempPath()
            })
            .Build();

        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        await using (var db = new ApplicationDbContext(dbOptions))
        {
            db.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = "project" });
            db.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "Notebook", Slug = "notebook" });
            var notebookFile = new NotebookFile
            {
                Id = fileId,
                NotebookId = notebookId,
                RelativePath = "docs/dsa.docx",
                FileSize = 10,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "old-hash"
            };
            notebookFile.GenerateDocumentId(notebookId);
            db.NotebookFiles.Add(notebookFile);
            db.NotebookFileMarkdownShadows.Add(new NotebookFileMarkdownShadow
            {
                OriginalNotebookFileId = fileId,
                ContentHash = "stale-hash",
                StoragePath = "stale.md",
                FileSize = 123,
                Status = MarkdownExtractionStatus.Completed,
                ErrorMessage = "stale",
                ProcessedAt = DateTime.UtcNow,
                IsIndexed = true
            });
            await db.SaveChangesAsync();
        }

        var service = new MarkdownExtractionService(
            scopeFactory,
            queue,
            Microsoft.Extensions.Options.Options.Create(new GuideAntsApi.Options.MarkdownExtractionOptions()),
            configuration,
            NullLogger<MarkdownExtractionService>.Instance);

        var result = await service.RetryNotebookExtractionAsync(fileId);

        result.Status.Should().Be(MarkdownExtractionStatus.Pending);
        await using var verifyDb = new ApplicationDbContext(dbOptions);
        var shadow = await verifyDb.NotebookFileMarkdownShadows.SingleAsync(s => s.OriginalNotebookFileId == fileId);
        shadow.Status.Should().Be(MarkdownExtractionStatus.Pending);
        shadow.ErrorMessage.Should().BeNull();
        shadow.ProcessedAt.Should().BeNull();
        shadow.IsIndexed.Should().BeFalse();
        queue.Enqueued.Should().ContainSingle();
        var queuedJob = queue.Enqueued.Single();
        queuedJob.JobType.Should().Be(nameof(ExtractNotebookFileMarkdownJob).Replace("Job", string.Empty));
        queuedJob.Payload.Should().BeOfType<ExtractNotebookFileMarkdownJob>()
            .Which.NotebookFileId.Should().Be(fileId);
    }

    private sealed class CapturingJobQueueService : IJobQueueService
    {
        public List<(string JobType, object Payload)> Enqueued { get; } = [];

        public Task<Guid> EnqueueAsync(string jobType, object payload, int priority = 0, DateTime? availableAt = null, Guid? correlationId = null, int? maxAttempts = null, CancellationToken ct = default)
        {
            Enqueued.Add((jobType, payload));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<JobQueue?> TryClaimAsync(string? jobType, int leaseSeconds, CancellationToken ct = default) => Task.FromResult<JobQueue?>(null);
        public Task<bool> CompleteAsync(Guid id, Guid claimToken, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> FailAsync(Guid id, Guid claimToken, string error, JobFailureClass failureClass = JobFailureClass.RetryableTransient, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> RequeueExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> RequeueAllProcessingAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> RenewLeaseAsync(Guid id, Guid claimToken, int additionalSeconds, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class DbContextOptionsScopeFactory : IServiceScopeFactory
    {
        private readonly DbContextOptions<ApplicationDbContext> _options;

        public DbContextOptionsScopeFactory(DbContextOptions<ApplicationDbContext> options)
        {
            _options = options;
        }

        public IServiceScope CreateScope() => new DbContextOptionsScope(_options);
    }

    private sealed class DbContextOptionsScope : IServiceScope
    {
        private readonly ApplicationDbContext _dbContext;

        public DbContextOptionsScope(DbContextOptions<ApplicationDbContext> options)
        {
            _dbContext = new ApplicationDbContext(options);
            ServiceProvider = new DbContextOptionsServiceProvider(_dbContext);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
            _dbContext.Dispose();
        }
    }

    private sealed class DbContextOptionsServiceProvider : IServiceProvider
    {
        private readonly ApplicationDbContext _dbContext;

        public DbContextOptionsServiceProvider(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(ApplicationDbContext) ? _dbContext : null;
        }
    }
}
