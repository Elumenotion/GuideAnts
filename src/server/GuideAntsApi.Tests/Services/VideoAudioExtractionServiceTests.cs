using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class VideoAudioExtractionServiceTests
{
    [TestMethod]
    public async Task ExtractAudioToTempFileAsync_StagesIntoSharedStorage_AndCleansUpOnDispose()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var client = new RecordingMediaExtractionClient(async request =>
            {
                request.SourcePath.Should().StartWith(".system/media-extract/");
                request.OutputPath.Should().StartWith(".system/media-extract/");

                var absoluteSource = ToAbsolutePath(storageRoot, request.SourcePath);
                File.Exists(absoluteSource).Should().BeTrue();

                var absoluteOutput = ToAbsolutePath(storageRoot, request.OutputPath);
                await File.WriteAllBytesAsync(absoluteOutput, new byte[] { 1, 2, 3, 4 });
                return new MediaExtractionResponse
                {
                    OutputPath = request.OutputPath,
                    ContentType = "audio/mpeg",
                    FileSize = 4
                };
            });

            var service = CreateService(storageRoot, client);
            await using var videoContent = new MemoryStream(new byte[] { 9, 8, 7, 6 });
            var extractedAudio = await service.ExtractAudioToTempFileAsync(videoContent, "clip.mp4");

            File.Exists(extractedAudio.AudioFilePath).Should().BeTrue();
            extractedAudio.FileSize.Should().Be(4);
            extractedAudio.ContentType.Should().Be("audio/mpeg");

            var workspacePath = Directory.GetParent(extractedAudio.AudioFilePath)!.FullName;
            Directory.Exists(workspacePath).Should().BeTrue();

            await extractedAudio.DisposeAsync();

            Directory.Exists(workspacePath).Should().BeFalse();
        }
        finally
        {
            CleanupDirectory(storageRoot);
        }
    }

    [TestMethod]
    public async Task ExtractAndStoreAudioAsync_UsesExistingSharedStorageSource_WithoutRestagingInput()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var sourcePath = Path.Combine(storageRoot, "project-a", "notebook-a", "source.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, new byte[] { 1, 2, 3 });

            var outputDirectory = Path.Combine(storageRoot, "project-a", "notebook-a", "Output");
            var client = new RecordingMediaExtractionClient(async request =>
            {
                request.SourcePath.Should().Be("project-a/notebook-a/source.mp4");
                request.OutputPath.Should().Be("project-a/notebook-a/Output/source_audio.mp3");

                var absoluteOutput = ToAbsolutePath(storageRoot, request.OutputPath);
                await File.WriteAllBytesAsync(absoluteOutput, new byte[] { 5, 6, 7, 8 });
                return new MediaExtractionResponse
                {
                    OutputPath = request.OutputPath,
                    ContentType = "audio/mpeg",
                    FileSize = 4
                };
            });

            var service = CreateService(storageRoot, client);
            var result = await service.ExtractAndStoreAudioAsync(sourcePath, outputDirectory, "source");

            result.audioFilePath.Should().Be(Path.Combine(outputDirectory, "source_audio.mp3"));
            result.fileSize.Should().Be(4);

            var transientRoot = Path.Combine(storageRoot, ".system", "media-extract");
            if (Directory.Exists(transientRoot))
            {
                Directory.EnumerateDirectories(transientRoot).Should().BeEmpty();
            }
        }
        finally
        {
            CleanupDirectory(storageRoot);
        }
    }

    [TestMethod]
    public async Task ExtractAudioToTempFileAsync_CleansWorkspace_WhenMediaExtractionFails()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var client = new RecordingMediaExtractionClient(_ =>
                throw new InvalidOperationException("media extraction failed"));

            var service = CreateService(storageRoot, client);
            await using var videoContent = new MemoryStream(new byte[] { 1, 2, 3 });

            var act = async () => await service.ExtractAudioToTempFileAsync(videoContent, "clip.mp4");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*media extraction failed*");

            var transientRoot = Path.Combine(storageRoot, ".system", "media-extract");
            if (Directory.Exists(transientRoot))
            {
                Directory.EnumerateDirectories(transientRoot).Should().BeEmpty();
            }
        }
        finally
        {
            CleanupDirectory(storageRoot);
        }
    }

    private static VideoAudioExtractionService CreateService(string storageRoot, IMediaExtractionClient client)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = storageRoot
            })
            .Build();

        return new VideoAudioExtractionService(
            Microsoft.Extensions.Options.Options.Create(new VideoAudioExtractionOptions
            {
                AudioFormat = "mp3",
                AudioQuality = "2",
                TimeoutSeconds = 1800,
                MaxFileSizeMB = 2048
            }),
            client,
            configuration,
            NullLogger<VideoAudioExtractionService>.Instance);
    }

    private static string CreateStorageRoot()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "guideants-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);
        return storageRoot;
    }

    private static string ToAbsolutePath(string storageRoot, string relativePath)
    {
        return Path.Combine(storageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CleanupDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingMediaExtractionClient : IMediaExtractionClient
    {
        private readonly Func<MediaExtractionRequest, Task<MediaExtractionResponse>> _handler;

        public RecordingMediaExtractionClient(Func<MediaExtractionRequest, Task<MediaExtractionResponse>> handler)
        {
            _handler = handler;
        }

        public Task<MediaExtractionResponse> ExtractAudioAsync(
            MediaExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _handler(request);
        }
    }
}
