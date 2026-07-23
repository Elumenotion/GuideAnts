using System.Text.Json;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookSyncMountReparseTests
{
    [TestMethod]
    public void DotNetEnumerateFiles_AllDirectories_DescendsIntoDirectoryJunction()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var tempRoot = CreateTempDirectory();
        try
        {
            var notebookRoot = Path.Combine(tempRoot, "notebook");
            var hostSource = Path.Combine(tempRoot, "host-source");
            Directory.CreateDirectory(notebookRoot);
            Directory.CreateDirectory(hostSource);
            File.WriteAllText(Path.Combine(notebookRoot, "local.txt"), "local");
            File.WriteAllText(Path.Combine(hostSource, "mounted-secret.txt"), "secret");

            var junctionPath = Path.Combine(notebookRoot, "Shared");
            Directory.CreateSymbolicLink(junctionPath, hostSource);
            File.GetAttributes(junctionPath).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();

            var defaultEnumeration = Directory
                .EnumerateFiles(notebookRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(notebookRoot, f).Replace('\\', '/'))
                .ToList();

            defaultEnumeration.Should().Contain("local.txt");
            defaultEnumeration.Should().Contain("Shared/mounted-secret.txt");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [TestMethod]
    public void NotebookSyncFileEnumerator_SkipsRegisteredMountContents()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var tempRoot = CreateTempDirectory();
        try
        {
            var notebookRoot = Path.Combine(tempRoot, "notebook");
            var hostSource = Path.Combine(tempRoot, "host-source");
            Directory.CreateDirectory(notebookRoot);
            Directory.CreateDirectory(hostSource);
            File.WriteAllText(Path.Combine(notebookRoot, "local.txt"), "local");
            File.WriteAllText(Path.Combine(hostSource, "mounted-secret.txt"), "secret");

            var junctionPath = Path.Combine(notebookRoot, "Shared");
            Directory.CreateSymbolicLink(junctionPath, hostSource);
            WriteMountsRegistry(notebookRoot, "Shared");

            var syncablePaths = NotebookSyncFileEnumerator.EnumerateSyncableRelativePaths(notebookRoot);

            syncablePaths.Should().ContainSingle().Which.Should().Be("local.txt");
            syncablePaths.Should().NotContain(p => p.StartsWith("Shared/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [TestMethod]
    public async Task NotebookFileSyncService_SyncNotebookAsync_DoesNotIndexRegisteredMountContents()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var tempRoot = CreateTempDirectory();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-mount-service-{Guid.NewGuid():N}");
        try
        {
            Guid projectId;
            Guid notebookId;
            await using (var seed = new ApplicationDbContext(options))
            {
                (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            }

            var notebookRoot = Path.Combine(tempRoot, "test-project", "test-notebook");
            var hostSource = Path.Combine(tempRoot, "host-source");
            Directory.CreateDirectory(notebookRoot);
            Directory.CreateDirectory(hostSource);
            await File.WriteAllTextAsync(Path.Combine(notebookRoot, "local.txt"), "local");
            await File.WriteAllTextAsync(Path.Combine(hostSource, "mounted-secret.txt"), "secret");

            Directory.CreateSymbolicLink(Path.Combine(notebookRoot, "Shared"), hostSource);
            WriteMountsRegistry(notebookRoot, "Shared");

            var pathResolver = new Mock<IStoragePathResolver>();
            pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

            await using var context = new ApplicationDbContext(options);
            var service = CreateNotebookFileSyncService(context, tempRoot, pathResolver);

            await service.ReconcileNotebookAsync(notebookId);

            var files = await context.NotebookFiles.Where(f => f.NotebookId == notebookId).ToListAsync();
            files.Should().ContainSingle(f => f.RelativePath == "local.txt");
            files.Should().NotContain(f => f.RelativePath.StartsWith("Shared/", StringComparison.OrdinalIgnoreCase));
            File.Exists(Path.Combine(hostSource, "mounted-secret.txt")).Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [TestMethod]
    public async Task SyncNotebookHandler_HandleAsync_DoesNotIndexRegisteredMountContents()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var tempRoot = CreateTempDirectory();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-mount-handler-{Guid.NewGuid():N}");
        try
        {
            Guid projectId;
            Guid notebookId;
            await using (var seed = new ApplicationDbContext(options))
            {
                (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            }

            var notebookRoot = Path.Combine(tempRoot, "test-project", "test-notebook");
            var hostSource = Path.Combine(tempRoot, "host-source");
            Directory.CreateDirectory(notebookRoot);
            Directory.CreateDirectory(hostSource);
            await File.WriteAllTextAsync(Path.Combine(notebookRoot, "notes.md"), "# local");
            await File.WriteAllTextAsync(Path.Combine(hostSource, "mounted-secret.txt"), "secret");

            Directory.CreateSymbolicLink(Path.Combine(notebookRoot, "Shared"), hostSource);
            WriteMountsRegistry(notebookRoot, "Shared");

            var reconciler = CreateReconciler(options, projectId, notebookId, notebookRoot);

            var handler = new SyncNotebookHandler(
                NullLogger<SyncNotebookHandler>.Instance,
                reconciler);

            var result = await handler.HandleAsync(new SyncNotebookJob(notebookId), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();

            await using var verify = new ApplicationDbContext(options);
            var files = await verify.NotebookFiles.Where(f => f.NotebookId == notebookId).ToListAsync();
            files.Should().ContainSingle(f => f.RelativePath == "notes.md");
            files.Should().NotContain(f => f.RelativePath.StartsWith("Shared/", StringComparison.OrdinalIgnoreCase));
            File.Exists(Path.Combine(hostSource, "mounted-secret.txt")).Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [TestMethod]
    public async Task SyncNotebookHandler_HandleAsync_RemovesStaleRowsUnderRegisteredMount_WithoutDeletingHostContent()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var tempRoot = CreateTempDirectory();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"sync-mount-stale-{Guid.NewGuid():N}");
        try
        {
            Guid projectId;
            Guid notebookId;
            await using (var seed = new ApplicationDbContext(options))
            {
                (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
                seed.NotebookFiles.Add(new NotebookFile
                {
                    Id = Guid.NewGuid(),
                    NotebookId = notebookId,
                    RelativePath = "Shared/previously-indexed.txt",
                    FileSize = 1,
                    LastModifiedUtc = DateTime.UtcNow,
                    FileHash = "stale",
                    Created = DateTime.UtcNow
                });
                await seed.SaveChangesAsync();
            }

            var notebookRoot = Path.Combine(tempRoot, "test-project", "test-notebook");
            var hostSource = Path.Combine(tempRoot, "host-source");
            Directory.CreateDirectory(notebookRoot);
            Directory.CreateDirectory(hostSource);
            var hostFile = Path.Combine(hostSource, "previously-indexed.txt");
            await File.WriteAllTextAsync(hostFile, "preserve-on-host");

            Directory.CreateSymbolicLink(Path.Combine(notebookRoot, "Shared"), hostSource);
            WriteMountsRegistry(notebookRoot, "Shared");

            var reconciler = CreateReconciler(options, projectId, notebookId, notebookRoot);

            var handler = new SyncNotebookHandler(
                NullLogger<SyncNotebookHandler>.Instance,
                reconciler);

            await handler.HandleAsync(new SyncNotebookJob(notebookId), CancellationToken.None);

            await using var verify = new ApplicationDbContext(options);
            var files = await verify.NotebookFiles.Where(f => f.NotebookId == notebookId).ToListAsync();
            files.Should().BeEmpty();
            File.Exists(hostFile).Should().BeTrue();
            File.ReadAllText(hostFile).Should().Be("preserve-on-host");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static NotebookFileSyncService CreateNotebookFileSyncService(
        ApplicationDbContext context,
        string storageRoot,
        Mock<IStoragePathResolver> pathResolver)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(context);
        providerMock.Setup(p => p.GetService(typeof(IJobQueueService)))
            .Returns(new BackgroundJobTestHelpers.CapturingJobQueueService());

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

        return new NotebookFileSyncService(
            reconciler,
            scopeFactoryMock.Object,
            NullLogger<NotebookFileSyncService>.Instance);
    }

    private static NotebookFileReconciler CreateReconciler(
        DbContextOptions<ApplicationDbContext> options,
        Guid projectId,
        Guid notebookId,
        string notebookRoot)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext)))
            .Returns(() => new ApplicationDbContext(options));
        providerMock.Setup(p => p.GetService(typeof(IJobQueueService)))
            .Returns(new BackgroundJobTestHelpers.CapturingJobQueueService());

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

        return new NotebookFileReconciler(
            scopeFactoryMock.Object,
            pathResolver.Object,
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            Mock.Of<IFileLineageService>(),
            Mock.Of<IUsageRecorder>(),
            new InMemoryNotebookLockService(),
            NullLogger<NotebookFileReconciler>.Instance);
    }

    private static void WriteMountsRegistry(string notebookRoot, string linkRelativePath)
    {
        var mountId = Guid.NewGuid();
        var registry = new
        {
            schemaVersion = 1,
            mounts = new[]
            {
                new
                {
                    mountId = mountId.ToString(),
                    leafName = Path.GetFileName(linkRelativePath),
                    linkRelativePath,
                    containerSourcePath = $"/app/HostMounts/{mountId:N}",
                    writable = true
                }
            }
        };

        var metadataDir = Path.Combine(notebookRoot, ".guideants");
        Directory.CreateDirectory(metadataDir);
        File.WriteAllText(
            Path.Combine(metadataDir, "mounts.json"),
            JsonSerializer.Serialize(registry));
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nb_sync_mount_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool CanCreateDirectorySymlinks()
    {
        var baseDir = CreateTempDirectory();
        var targetDir = Path.Combine(baseDir, "target");
        var linkDir = Path.Combine(baseDir, "link");
        Directory.CreateDirectory(targetDir);

        try
        {
            Directory.CreateSymbolicLink(linkDir, targetDir);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDeleteDirectory(baseDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
