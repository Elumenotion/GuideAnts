using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookFileHashTests
{
    [TestMethod]
    public void IsUnchanged_True_WhenSizeAndWholeSecondMtimeMatch_WithRealHash()
    {
        var mtime = new DateTime(2026, 8, 25, 14, 30, 45, 123, DateTimeKind.Utc);
        var sameSecondDifferentMs = new DateTime(2026, 8, 25, 14, 30, 45, 987, DateTimeKind.Utc);

        NotebookFileHash.IsUnchanged(
                dbSize: 100,
                dbLastModifiedUtc: mtime,
                dbFileHash: "ABC123",
                diskSize: 100,
                diskLastModifiedUtc: sameSecondDifferentMs)
            .Should().BeTrue();
    }

    [TestMethod]
    public void IsUnchanged_False_WhenPlaceholderHash()
    {
        var mtime = new DateTime(2026, 8, 25, 14, 30, 45, DateTimeKind.Utc);

        NotebookFileHash.IsUnchanged(
                dbSize: 100,
                dbLastModifiedUtc: mtime,
                dbFileHash: NotebookFileHash.Placeholder(100, mtime),
                diskSize: 100,
                diskLastModifiedUtc: mtime)
            .Should().BeFalse();
    }

    [TestMethod]
    public void IsUnchanged_False_WhenSizeDiffers()
    {
        var mtime = new DateTime(2026, 8, 25, 14, 30, 45, DateTimeKind.Utc);

        NotebookFileHash.IsUnchanged(
                dbSize: 100,
                dbLastModifiedUtc: mtime,
                dbFileHash: "ABC123",
                diskSize: 101,
                diskLastModifiedUtc: mtime)
            .Should().BeFalse();
    }

    [TestMethod]
    public void IsUnchanged_False_WhenWholeSecondMtimeDiffers()
    {
        var mtime = new DateTime(2026, 8, 25, 14, 30, 45, DateTimeKind.Utc);
        var nextSecond = mtime.AddSeconds(1);

        NotebookFileHash.IsUnchanged(
                dbSize: 100,
                dbLastModifiedUtc: mtime,
                dbFileHash: "ABC123",
                diskSize: 100,
                diskLastModifiedUtc: nextSecond)
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task RegisterFilesAsync_DoesNotDowngradeRealHashWhenMetadataUnchanged()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"register-keep-hash-{Guid.NewGuid():N}");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"register-keep-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        Guid projectId;
        Guid notebookId;
        try
        {
            await using (var seed = new ApplicationDbContext(options))
            {
                (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            }

            var notebookRoot = Path.Combine(tempRoot, "test-project", "test-notebook");
            Directory.CreateDirectory(notebookRoot);
            var relativePath = "Output/keep-hash.txt";
            var fullPath = Path.Combine(notebookRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, "stable");

            var pathResolver = new Mock<IStoragePathResolver>();
            pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

            var providerMock = new Mock<IServiceProvider>();
            providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext)))
                .Returns(() => new ApplicationDbContext(options));
            var scopeMock = new Mock<IServiceScope>();
            scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);
            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var reconciler = new NotebookFileReconciler(
                scopeFactoryMock.Object,
                pathResolver.Object,
                new BackgroundJobTestHelpers.CapturingJobQueueService(),
                Mock.Of<IFileLineageService>(),
                Mock.Of<IUsageRecorder>(),
                new InMemoryNotebookLockService(),
                NullLogger<NotebookFileReconciler>.Instance);

            await reconciler.ReconcileNotebookAsync(notebookId);

            await using var afterHash = new ApplicationDbContext(options);
            var hashed = await afterHash.NotebookFiles.SingleAsync(f => f.NotebookId == notebookId);
            var realHash = hashed.FileHash;
            NotebookFileHash.IsPlaceholder(realHash).Should().BeFalse();

            await reconciler.RegisterFilesAsync(notebookId, [relativePath]);

            await using var afterRegister = new ApplicationDbContext(options);
            var row = await afterRegister.NotebookFiles.SingleAsync(f => f.NotebookId == notebookId);
            row.FileHash.Should().Be(realHash);
            NotebookFileHash.IsPlaceholder(row.FileHash).Should().BeFalse();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
