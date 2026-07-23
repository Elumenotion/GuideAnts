using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Sync;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookFileRegisterServingTests
{
    [TestMethod]
    public async Task RegisterFilesAsync_AllowsContentAndTreeBeforeFullReconcile()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"register-serving-{Guid.NewGuid():N}");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"register-serving-{Guid.NewGuid():N}");
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
            var relativePath = "Output/generated.png";
            var fullPath = Path.Combine(notebookRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, [0x89, 0x50, 0x4E, 0x47]);

            var pathResolver = new Mock<IStoragePathResolver>();
            pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

            var providerMock = new Mock<IServiceProvider>();
            providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext)))
                .Returns(() => new ApplicationDbContext(options));
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
                NullLogger<NotebookFileReconciler>.Instance);

            var syncService = new NotebookFileSyncService(
                reconciler,
                scopeFactoryMock.Object,
                Mock.Of<INotebookLockService>(),
                NullLogger<NotebookFileSyncService>.Instance);

            await syncService.RegisterFilesAsync(notebookId, [relativePath]);

            await using var verify = new ApplicationDbContext(options);
            var row = await verify.NotebookFiles
                .SingleAsync(f => f.NotebookId == notebookId && f.RelativePath == relativePath);
            NotebookFileHash.IsPlaceholder(row.FileHash).Should().BeTrue();

            var fileService = new NotebookFileService(
                scopeFactoryMock.Object,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:Path"] = tempRoot
                }).Build(),
                syncService,
                NullLogger<NotebookFileService>.Instance,
                Mock.Of<IFileLineageService>(),
                Mock.Of<IContentFileService>(),
                Mock.Of<IMarkdownExtractionService>(),
                pathResolver.Object);

            var (stream, contentType) = await fileService.GetFileContentStreamAsync(projectId, notebookId, relativePath);
            stream.Should().NotBeNull();
            contentType.Should().NotBeNullOrWhiteSpace();
            await stream.DisposeAsync();

            var tree = await fileService.GetFolderTreeAsync(projectId, notebookId);
            tree.Should().NotBeNull();
            EnumerateFilesRecursively(tree!).Should()
                .Contain(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

            var reconcileResult = await reconciler.ReconcileNotebookAsync(notebookId);
            reconcileResult.Updated.Should().Be(1);

            await using var afterReconcile = new ApplicationDbContext(options);
            var updated = await afterReconcile.NotebookFiles.SingleAsync(f => f.Id == row.Id);
            NotebookFileHash.IsPlaceholder(updated.FileHash).Should().BeFalse();
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
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

    [TestMethod]
    public async Task RegisterFilesAsync_SkipsMissingPathWithoutThrowing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"register-missing-{Guid.NewGuid():N}");
        Guid projectId;
        Guid notebookId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
        }

        var notebookRoot = Path.Combine(Path.GetTempPath(), $"register-missing-{Guid.NewGuid():N}", "test-project", "test-notebook");
        Directory.CreateDirectory(notebookRoot);

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
            NullLogger<NotebookFileReconciler>.Instance);

        await reconciler.RegisterFilesAsync(notebookId, ["Output/missing.png"]);

        await using var verify = new ApplicationDbContext(options);
        (await verify.NotebookFiles.CountAsync(f => f.NotebookId == notebookId)).Should().Be(0);
    }

    private static IEnumerable<NotebookFileDto> EnumerateFilesRecursively(NotebookFolderTreeDto node)
    {
        foreach (var file in node.Files)
        {
            yield return file;
        }

        foreach (var folder in node.SubFolders)
        {
            foreach (var nested in EnumerateFilesRecursively(folder))
            {
                yield return nested;
            }
        }
    }
}
