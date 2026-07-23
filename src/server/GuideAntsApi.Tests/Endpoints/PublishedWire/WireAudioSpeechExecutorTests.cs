using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;

namespace GuideAntsApi.Tests.Endpoints.PublishedWire;

[TestClass]
public sealed class WireAudioSpeechExecutorTests
{
    [TestMethod]
    public async Task ExecuteAsync_SandboxPath_WritesToNotebookOutputWithoutDatabaseSync()
    {
        using var storage = new TempStorage();
        var configuration = BuildConfiguration(storage.Root);
        var speechService = new Mock<ISpeechSynthesisService>(MockBehavior.Strict);
        var syncService = new Mock<INotebookFileSyncService>(MockBehavior.Strict);
        var notebookId = Guid.NewGuid();
        var runContext = new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid());
        string? capturedOutputPath = null;

        speechService
            .Setup(s => s.SynthesizeToWavAsync(
                "hello",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, path, _) =>
            {
                capturedOutputPath = path;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            })
            .ReturnsAsync(new ISpeechSynthesisService.SpeechSynthesisResult(
                Success: true,
                DurationSeconds: 2,
                ErrorMessage: null,
                ProviderId: "SpeechProvider"));

        var http = new DefaultHttpContext();
        var mode = new ServiceMode("speech-default", "SpeechSection", "tts-test", null, Enabled: true, IsDefault: true);

        var result = await WireAudioSpeechExecutor.ExecuteAsync(
            http,
            new WireAudioSpeechExecutor.Request("hello", runContext),
            mode,
            speechService.Object,
            configuration,
            syncService.Object,
            syncDatabaseAfterWrite: false,
            recordUsageAsync: (_, _, _) => Task.CompletedTask);

        result.Should().NotBeNull();
        capturedOutputPath.Should().NotBeNullOrWhiteSpace();
        var expectedOutputDir = Path.GetFullPath(storage.OutputDir(runContext.ProjectId, notebookId));
        Path.GetFullPath(capturedOutputPath!).Should().StartWith(expectedOutputDir);
        Path.GetFileName(capturedOutputPath).Should().StartWith("wire-").And.EndWith(".wav");
        File.Exists(capturedOutputPath).Should().BeTrue();
        syncService.Verify(s => s.QueueReconcileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        speechService.VerifyAll();
    }

    [TestMethod]
    public async Task ExecuteAsync_PublishedPath_RegistersAndQueuesSyncAfterWrite()
    {
        using var storage = new TempStorage();
        var configuration = BuildConfiguration(storage.Root);
        var speechService = new Mock<ISpeechSynthesisService>(MockBehavior.Strict);
        var syncService = new Mock<INotebookFileSyncService>(MockBehavior.Strict);
        var notebookId = Guid.NewGuid();
        var runContext = new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()) { IsPublished = true };

        speechService
            .Setup(s => s.SynthesizeToWavAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, path, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[] { 9, 8, 7 });
            })
            .ReturnsAsync(new ISpeechSynthesisService.SpeechSynthesisResult(
                Success: true,
                DurationSeconds: 2,
                ErrorMessage: null,
                ProviderId: "SpeechProvider"));

        syncService
            .Setup(s => s.RegisterFilesAsync(notebookId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        syncService
            .Setup(s => s.QueueReconcileAsync(notebookId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var http = new DefaultHttpContext();
        var mode = new ServiceMode("speech-default", "SpeechSection", "tts-test", null, Enabled: true, IsDefault: true);

        await WireAudioSpeechExecutor.ExecuteAsync(
            http,
            new WireAudioSpeechExecutor.Request("hello", runContext),
            mode,
            speechService.Object,
            configuration,
            syncService.Object,
            syncDatabaseAfterWrite: true,
            recordUsageAsync: (_, _, _) => Task.CompletedTask);

        syncService.Verify(s => s.RegisterFilesAsync(notebookId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        syncService.Verify(s => s.QueueReconcileAsync(notebookId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IConfiguration BuildConfiguration(string storageRoot)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = storageRoot
            })
            .Build();
    }

    private sealed class TempStorage : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "guideants-wire-speech-" + Guid.NewGuid().ToString("N"));

        public TempStorage() => Directory.CreateDirectory(Root);

        public string OutputDir(Guid projectId, Guid notebookId)
        {
            var dir = Path.Combine(Root, projectId.ToString(), "notebooks", notebookId.ToString(), "Output");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
