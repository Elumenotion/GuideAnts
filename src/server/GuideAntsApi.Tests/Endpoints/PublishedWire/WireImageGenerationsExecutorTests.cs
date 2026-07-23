using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;

namespace GuideAntsApi.Tests.Endpoints.PublishedWire;

[TestClass]
public sealed class WireImageGenerationsExecutorTests
{
    [TestMethod]
    public async Task ExecuteAsync_SandboxPath_WritesWithoutDatabaseSync()
    {
        var imageBytes = new byte[] { 1, 2, 3 };
        var imageService = new Mock<INotebookImageService>(MockBehavior.Strict);
        var syncService = new Mock<INotebookFileSyncService>(MockBehavior.Strict);
        var notebookId = Guid.NewGuid();
        var runContext = new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid());

        imageService
            .Setup(s => s.GenerateImageBytesAsync(
                "aqua square",
                "1024x1024",
                1,
                "png",
                runContext,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(imageBytes);

        imageService
            .Setup(s => s.WriteImageBytesToNotebookOutputAsync(
                imageBytes,
                It.Is<string>(name => name.StartsWith("wire-", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal)),
                runContext,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var http = new DefaultHttpContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = Path.GetTempPath() })
            .Build();
        var mode = new ServiceMode("img-default", "ImageSection", "dall-e-test", null, Enabled: true, IsDefault: true);
        var usageRecorded = false;

        var result = await WireImageGenerationsExecutor.ExecuteAsync(
            http,
            new WireImageGenerationsExecutor.Request("aqua square", "1024x1024", 1, runContext),
            mode,
            configuration,
            imageService.Object,
            syncService.Object,
            syncDatabaseAfterWrite: false,
            recordUsageAsync: (_, _, _) =>
            {
                usageRecorded = true;
                return Task.CompletedTask;
            });

        result.Should().NotBeNull();
        usageRecorded.Should().BeTrue();
        imageService.VerifyAll();
        syncService.Verify(
            s => s.QueueReconcileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_PublishedPath_RegistersAndQueuesSyncAfterWrite()
    {
        var imageBytes = new byte[] { 9, 8, 7 };
        var imageService = new Mock<INotebookImageService>(MockBehavior.Strict);
        var syncService = new Mock<INotebookFileSyncService>(MockBehavior.Strict);
        var notebookId = Guid.NewGuid();
        var runContext = new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()) { IsPublished = true };

        imageService
            .Setup(s => s.GenerateImageBytesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                runContext,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(imageBytes);

        imageService
            .Setup(s => s.WriteImageBytesToNotebookOutputAsync(
                imageBytes,
                It.IsAny<string>(),
                runContext,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        syncService
            .Setup(s => s.RegisterFilesAsync(notebookId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        syncService
            .Setup(s => s.QueueReconcileAsync(notebookId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var http = new DefaultHttpContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = Path.GetTempPath() })
            .Build();
        var mode = new ServiceMode("img-default", "ImageSection", "dall-e-test", null, Enabled: true, IsDefault: true);

        await WireImageGenerationsExecutor.ExecuteAsync(
            http,
            new WireImageGenerationsExecutor.Request("prompt", "1024x1024", 1, runContext),
            mode,
            configuration,
            imageService.Object,
            syncService.Object,
            syncDatabaseAfterWrite: true,
            recordUsageAsync: (_, _, _) => Task.CompletedTask);

        syncService.Verify(s => s.RegisterFilesAsync(notebookId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        syncService.Verify(s => s.QueueReconcileAsync(notebookId, It.IsAny<CancellationToken>()), Times.Once);
        imageService.Verify(
            s => s.GenerateImageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<InvocationContext>()),
            Times.Never);
    }
}
