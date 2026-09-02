using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services;

/// <summary>
/// Deep coverage for <see cref="VideoAudioExtractionService"/>: support gating, file-size
/// guard, the out-of-storage-root staging branch, default content-type, and the
/// post-extraction output validation failures. The media extraction itself (ffmpeg) is faked
/// via <see cref="IMediaExtractionClient"/>.
/// </summary>
[TestClass]
public sealed class VideoAudioExtractionServiceDeepTests
{
    [TestMethod]
    public void IsVideoFileSupported_RecognizesContentTypeAndExtension()
    {
        var service = CreateService(CreateStorageRoot(), new RecordingMediaExtractionClient(_ => throw new InvalidOperationException()));

        service.IsVideoFileSupported("a.bin", "video/mp4").Should().BeTrue();
        service.IsVideoFileSupported("clip.mkv", "").Should().BeTrue();
        service.IsVideoFileSupported("clip.txt", "text/plain").Should().BeFalse();
        service.IsVideoFileSupported("noext", "").Should().BeFalse();
        service.IsVideoFileSupported("clip.webm", "video/webm").Should().BeTrue();
        service.IsVideoFileSupported("recording.webm", "audio/webm").Should().BeFalse();
        service.IsVideoFileSupported("recording.webm", "audio/webm;codecs=opus").Should().BeFalse();
    }

    [TestMethod]
    public void IsFileSizeSupported_RespectsConfiguredMaximum()
    {
        var service = CreateService(CreateStorageRoot(), new RecordingMediaExtractionClient(_ => throw new InvalidOperationException()), maxFileSizeMB: 1);

        service.IsFileSizeSupported(1024).Should().BeTrue();
        service.IsFileSizeSupported(2L * 1024 * 1024).Should().BeFalse();
    }

    [TestMethod]
    public async Task ExtractAndStoreAudioAsync_Throws_WhenVideoFileMissing()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var service = CreateService(storageRoot, new RecordingMediaExtractionClient(_ => throw new InvalidOperationException()));
            var act = async () => await service.ExtractAndStoreAudioAsync(
                Path.Combine(storageRoot, "missing.mp4"),
                Path.Combine(storageRoot, "out"),
                "missing");

            await act.Should().ThrowAsync<FileNotFoundException>();
        }
        finally
        {
            Cleanup(storageRoot);
        }
    }

    [TestMethod]
    public async Task ExtractAndStoreAudioAsync_StagesSourceFromOutsideStorageRoot()
    {
        var storageRoot = CreateStorageRoot();
        var externalDir = Path.Combine(Path.GetTempPath(), "guideants-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);
        var sourcePath = Path.Combine(externalDir, "external.mp4");
        await File.WriteAllBytesAsync(sourcePath, new byte[] { 1, 2, 3 });
        try
        {
            var client = new RecordingMediaExtractionClient(async request =>
            {
                request.SourcePath.Should().StartWith(".system/media-extract/");
                var absoluteOutput = Path.Combine(storageRoot, request.OutputPath.Replace('/', Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(absoluteOutput, new byte[] { 9, 9 });
                return new MediaExtractionResponse { OutputPath = request.OutputPath, ContentType = "audio/mpeg", FileSize = 2 };
            });
            var outputDirectory = Path.Combine(storageRoot, "proj", "Output");
            var service = CreateService(storageRoot, client);

            var result = await service.ExtractAndStoreAudioAsync(sourcePath, outputDirectory, "external");

            result.fileSize.Should().Be(2);

            var transientRoot = Path.Combine(storageRoot, ".system", "media-extract");
            if (Directory.Exists(transientRoot))
            {
                Directory.EnumerateDirectories(transientRoot).Should().BeEmpty();
            }
        }
        finally
        {
            Cleanup(storageRoot);
            Cleanup(externalDir);
        }
    }

    [TestMethod]
    public async Task ExtractAudioToTempFileAsync_DefaultsContentType_WhenResponseMissingIt()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var client = new RecordingMediaExtractionClient(async request =>
            {
                var absoluteOutput = Path.Combine(storageRoot, request.OutputPath.Replace('/', Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(absoluteOutput, new byte[] { 1, 2, 3 });
                return new MediaExtractionResponse { OutputPath = request.OutputPath, ContentType = "", FileSize = 3 };
            });
            var service = CreateService(storageRoot, client);
            await using var content = new MemoryStream(new byte[] { 4, 5 });

            await using var result = await service.ExtractAudioToTempFileAsync(content, "clip.mp4");

            result.ContentType.Should().Be("audio/mpeg");
        }
        finally
        {
            Cleanup(storageRoot);
        }
    }

    [TestMethod]
    public async Task ExtractAudioToTempFileAsync_Throws_WhenOutputMissing()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var client = new RecordingMediaExtractionClient(_ =>
                Task.FromResult(new MediaExtractionResponse { OutputPath = "x", ContentType = "audio/mpeg", FileSize = 0 }));
            var service = CreateService(storageRoot, client);
            await using var content = new MemoryStream(new byte[] { 4, 5 });

            var act = async () => await service.ExtractAudioToTempFileAsync(content, "clip.mp4");

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*output file was not found*");
        }
        finally
        {
            Cleanup(storageRoot);
        }
    }

    [TestMethod]
    public async Task ExtractAudioToTempFileAsync_Throws_WhenOutputEmpty()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var client = new RecordingMediaExtractionClient(async request =>
            {
                var absoluteOutput = Path.Combine(storageRoot, request.OutputPath.Replace('/', Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(absoluteOutput, Array.Empty<byte>());
                return new MediaExtractionResponse { OutputPath = request.OutputPath, ContentType = "audio/mpeg", FileSize = 0 };
            });
            var service = CreateService(storageRoot, client);
            await using var content = new MemoryStream(new byte[] { 4, 5 });

            var act = async () => await service.ExtractAudioToTempFileAsync(content, "clip.mp4");

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*output file is empty*");
        }
        finally
        {
            Cleanup(storageRoot);
        }
    }

    [TestMethod]
    public async Task ExtractAudioToTempFileAsync_Throws_WhenVideoContentNull()
    {
        var storageRoot = CreateStorageRoot();
        try
        {
            var service = CreateService(storageRoot, new RecordingMediaExtractionClient(_ => throw new InvalidOperationException()));
            var act = async () => await service.ExtractAudioToTempFileAsync(null!, "clip.mp4");
            await act.Should().ThrowAsync<ArgumentNullException>();
        }
        finally
        {
            Cleanup(storageRoot);
        }
    }

    private static VideoAudioExtractionService CreateService(string storageRoot, IMediaExtractionClient client, int maxFileSizeMB = 2048)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = storageRoot })
            .Build();

        return new VideoAudioExtractionService(
            Microsoft.Extensions.Options.Options.Create(new VideoAudioExtractionOptions
            {
                AudioFormat = "mp3",
                AudioQuality = "2",
                TimeoutSeconds = 1800,
                MaxFileSizeMB = maxFileSizeMB
            }),
            client,
            configuration,
            NullLogger<VideoAudioExtractionService>.Instance);
    }

    private static string CreateStorageRoot()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "guideants-media-deep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);
        return storageRoot;
    }

    private static void Cleanup(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingMediaExtractionClient(Func<MediaExtractionRequest, Task<MediaExtractionResponse>> handler) : IMediaExtractionClient
    {
        public Task<MediaExtractionResponse> ExtractAudioAsync(MediaExtractionRequest request, CancellationToken cancellationToken = default)
            => handler(request);
    }
}
