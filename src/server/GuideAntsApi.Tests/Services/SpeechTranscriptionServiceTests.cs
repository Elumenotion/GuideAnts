using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SpeechTranscriptionServiceTests
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechTranscriptionBaseUrl";
    private const string GoogleProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";

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
                ApiKey = "test-key"
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
                ApiKey = "azure-key"
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
    public async Task TranscribeAudioWithDurationAsync_StripsAudioContentTypeParameters_ForAzureSpeechProvider()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"durationMilliseconds\":1200,\"phrases\":[{\"offsetMilliseconds\":0,\"durationMilliseconds\":1200,\"text\":\"hello azure\",\"speaker\":0}]}",
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
                ApiKey = "azure-key"
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
        await service.TranscribeAudioWithDurationAsync(audio, "recording.webm", "audio/webm;codecs=opus");

        handler.LastRequestBody.Should().Contain("Content-Type: audio/webm");
        handler.LastRequestBody.Should().NotContain("audio/webm;codecs=opus");
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
                ApiKey = "test-key"
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

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_UsesGoogleProviderWithoutHuggingFaceValidationCrossWire()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hello google\"}]}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: GoogleProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://azure-speech.example.com", ApiKey = "unused" },
            transcriptionOptions: new SpeechTranscriptionOptions { TimeoutSeconds = 120 },
            localServiceHostsOptions: new LocalServiceHostsOptions(),
            configurationValues: new Dictionary<string, string?>
            {
                ["GoogleGeminiApi:ApiKey"] = "gemini-key",
            },
            modelId: "gemini-2.5-flash");

        await using var audio = new MemoryStream(new byte[1024]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "recording.wav", "audio/wav");

        result.Text.Should().Be("hello google");
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent");
        handler.LastRequestBody.Should().Contain("Transcribe this audio. Return only the transcript.");
        handler.LastRequestBody.Should().Contain("\"inlineData\"");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_UsesHuggingFaceProviderWithoutOpenRouterValidationCrossWire()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"text\":\"hello hugging face\"}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: HuggingFaceProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://azure-speech.example.com", ApiKey = "unused" },
            transcriptionOptions: new SpeechTranscriptionOptions { TimeoutSeconds = 120 },
            localServiceHostsOptions: new LocalServiceHostsOptions(),
            configurationValues: new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token",
            },
            modelId: "hf-asr-model");

        await using var audio = new MemoryStream(new byte[1024]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "recording.wav", "audio/wav");

        result.Text.Should().Be("hello hugging face");
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://api-inference.huggingface.co/models/hf-asr-model");
        handler.LastRequestHeaders.Should().ContainKey("Authorization");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_UsesOpenRouterProviderWithChatCompletionsAudioPayload()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"hello openrouter\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: OpenRouterProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://azure-speech.example.com", ApiKey = "unused" },
            transcriptionOptions: new SpeechTranscriptionOptions { TimeoutSeconds = 120 },
            localServiceHostsOptions: new LocalServiceHostsOptions(),
            configurationValues: new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
            },
            modelId: "openai/whisper-1");

        await using var audio = new MemoryStream(new byte[1024]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "recording.wav", "audio/wav");

        result.Text.Should().Be("hello openrouter");
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
        handler.LastRequestBody.Should().Contain("\"input_audio\"");
    }

    private static SpeechTranscriptionService CreateService(
        HttpClient httpClient,
        string providerSection,
        AzureSpeechServiceOptions azureOptions,
        SpeechTranscriptionOptions transcriptionOptions,
        LocalServiceHostsOptions localServiceHostsOptions,
        Mock<IVideoAudioExtractionService>? videoService = null,
        IDictionary<string, string?>? configurationValues = null,
        string? modelId = null)
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

        var resolver = modelId is null
            ? new FakeServiceModeResolver(
                RoutedServiceNames.SpeechTranscription,
                providerSection: providerSection)
            : new FakeServiceModeResolver(
                (RoutedServiceNames.SpeechTranscription, new ServiceMode(
                    ModeId: "default",
                    ProviderSection: providerSection,
                    ModelId: modelId,
                    RequestPresetJson: null,
                    Enabled: true,
                    IsDefault: true)));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        return new SpeechTranscriptionService(
            httpClient,
            speechOptionsMonitor.Object,
            transcriptionOptionsMonitor.Object,
            localServiceHostsOptionsMonitor.Object,
            extractionOptionsMonitor.Object,
            effectiveVideoService.Object,
            resolver,
            configuration,
            NullLogger<SpeechTranscriptionService>.Instance);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public Dictionary<string, string> LastRequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestHeaders.Clear();
            foreach (var header in request.Headers)
            {
                LastRequestHeaders[header.Key] = string.Join(",", header.Value);
            }

            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _responder(request);
        }
    }
}
