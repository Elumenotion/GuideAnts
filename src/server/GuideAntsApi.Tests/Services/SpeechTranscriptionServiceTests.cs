using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SpeechTranscriptionServiceTests
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechTranscriptionBaseUrl";

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_UsesLocalAsrProvider_WhenModeSelectsLocal()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"text\":\"hello local asr\",\"durationSeconds\":7,\"modelRef\":\"/models-local/asr/Qwen3-ASR-0.6B\"}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: LocalProviderSection,
            azureOptions: new AzureSpeechServiceOptions
            {
                Endpoint = "https://azure-speech.example.com",
                ApiKey = "test-key",
                TimeoutSeconds = 90
            },
            transcriptionOptions: new SpeechTranscriptionOptions
            {
                TimeoutSeconds = 120
            },
            localServiceHostsOptions: new LocalServiceHostsOptions
            {
                SpeechTranscriptionBaseUrl = "http://guideants-ai:80"
            });

        await using var audio = new MemoryStream(new byte[1024]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "recording.wav", "audio/wav");

        result.Text.Should().Be("hello local asr");
        result.DurationSeconds.Should().Be(7);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("http://guideants-ai/asr/transcribe");
        handler.LastRequestHeaders.Should().ContainKey("x-request-id");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_UsesAzureSpeechProvider_WhenModeSelectsAzure()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"durationMilliseconds\":3200,\"phrases\":[{\"offsetMilliseconds\":0,\"durationMilliseconds\":3200,\"text\":\"hello azure\",\"speaker\":0}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: AzureProviderSection,
            azureOptions: new AzureSpeechServiceOptions
            {
                Endpoint = "https://azure-speech.example.com/",
                ApiKey = "azure-key",
                TimeoutSeconds = 90
            },
            transcriptionOptions: new SpeechTranscriptionOptions
            {
                TimeoutSeconds = 120
            },
            localServiceHostsOptions: new LocalServiceHostsOptions
            {
                SpeechTranscriptionBaseUrl = "http://guideants-ai:80"
            });

        await using var audio = new MemoryStream(new byte[1024]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "recording.wav", "audio/wav");

        result.Text.Should().Be("**Speaker 1:** hello azure");
        result.DurationSeconds.Should().Be(3);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://azure-speech.example.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15");
        handler.LastRequestHeaders.Should().ContainKey("Ocp-Apim-Subscription-Key");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_ExtractsVideoViaSharedStorageFlow()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"text\":\"video transcript\",\"durationSeconds\":11,\"modelRef\":\"/models-local/asr/Qwen3-ASR-0.6B\"}",
                    Encoding.UTF8,
                    "application/json")
            });

        var tempDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "speech-video-" + Guid.NewGuid().ToString("N")));
        var tempAudioPath = Path.Combine(tempDirectory.FullName, "output.mp3");
        await File.WriteAllBytesAsync(tempAudioPath, new byte[] { 1, 2, 3, 4 });

        using var httpClient = new HttpClient(handler);
        var videoService = new Mock<IVideoAudioExtractionService>();
        videoService.Setup(x => x.IsVideoFileSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        videoService.Setup(x => x.ExtractAudioToTempFileAsync(
                It.IsAny<Stream>(),
                "clip.mp4",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransientAudioExtractionResult(
                tempAudioPath,
                4,
                "audio/mpeg",
                () =>
                {
                    if (Directory.Exists(tempDirectory.FullName))
                    {
                        Directory.Delete(tempDirectory.FullName, recursive: true);
                    }

                    return ValueTask.CompletedTask;
                }));

        var service = CreateService(
            httpClient,
            providerSection: LocalProviderSection,
            azureOptions: new AzureSpeechServiceOptions
            {
                Endpoint = "https://azure-speech.example.com",
                ApiKey = "test-key",
                TimeoutSeconds = 90
            },
            transcriptionOptions: new SpeechTranscriptionOptions
            {
                TimeoutSeconds = 120
            },
            localServiceHostsOptions: new LocalServiceHostsOptions
            {
                SpeechTranscriptionBaseUrl = "http://guideants-ai:80",
                MediaBaseUrl = "http://guideants-ai:80"
            },
            videoService: videoService);

        await using var video = new MemoryStream(new byte[2048]);
        var result = await service.TranscribeAudioWithDurationAsync(video, "clip.mp4", "video/mp4");

        result.Text.Should().Be("video transcript");
        result.DurationSeconds.Should().Be(11);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("http://guideants-ai/asr/transcribe");
        videoService.Verify(x => x.ExtractAudioToTempFileAsync(
            It.IsAny<Stream>(),
            "clip.mp4",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static SpeechTranscriptionService CreateService(
        HttpClient httpClient,
        string providerSection,
        AzureSpeechServiceOptions azureOptions,
        SpeechTranscriptionOptions transcriptionOptions,
        LocalServiceHostsOptions localServiceHostsOptions,
        Mock<IVideoAudioExtractionService>? videoService = null)
    {
        var speechOptionsMonitor = new Mock<IOptionsMonitor<AzureSpeechServiceOptions>>();
        speechOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(azureOptions);

        var transcriptionOptionsMonitor = new Mock<IOptionsMonitor<SpeechTranscriptionOptions>>();
        transcriptionOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(transcriptionOptions);

        var localServiceHostsOptionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        localServiceHostsOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(localServiceHostsOptions);

        var extractionOptionsMonitor = new Mock<IOptionsMonitor<MarkdownExtractionOptions>>();
        extractionOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new MarkdownExtractionOptions
        {
            MaxFileSizeMB = 500,
            SupportedExtensions = [".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".webm", ".opus"]
        });

        var effectiveVideoService = videoService ?? new Mock<IVideoAudioExtractionService>();
        if (videoService is null)
        {
            effectiveVideoService
                .Setup(x => x.IsVideoFileSupported(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(false);
        }

        var resolver = new FakeServiceModeResolver(
            RoutedServiceNames.SpeechTranscription,
            providerSection: providerSection);

        return new SpeechTranscriptionService(
            httpClient,
            speechOptionsMonitor.Object,
            transcriptionOptionsMonitor.Object,
            localServiceHostsOptionsMonitor.Object,
            extractionOptionsMonitor.Object,
            effectiveVideoService.Object,
            resolver,
            NullLogger<SpeechTranscriptionService>.Instance);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public Uri? LastRequestUri { get; private set; }
        public Dictionary<string, string> LastRequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestHeaders.Clear();
            foreach (var header in request.Headers)
            {
                LastRequestHeaders[header.Key] = string.Join(",", header.Value);
            }

            return Task.FromResult(_responder(request));
        }
    }
}
