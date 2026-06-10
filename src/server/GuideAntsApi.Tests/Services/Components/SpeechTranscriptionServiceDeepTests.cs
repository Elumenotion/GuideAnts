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
using Moq;

namespace GuideAntsApi.Tests.Services.Components;

/// <summary>
/// Deep provider coverage for <see cref="SpeechTranscriptionService"/>: the video
/// extraction path, Google Gemini / Hugging Face / OpenRouter / local ASR provider
/// flows, Azure diarized formatting and transient retry, and the timeout wrapper.
/// HTTP is faked with an <see cref="HttpMessageHandler"/>; the ffmpeg-backed video
/// audio extraction is faked through <see cref="IVideoAudioExtractionService"/>.
/// </summary>
[TestClass]
public sealed class SpeechTranscriptionServiceDeepTests
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechTranscriptionBaseUrl";
    private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";

    [TestMethod]
    public async Task TranscribeVideo_Local_ExtractsAudioToTempFile_ThenTranscribes()
    {
        var tempAudioPath = Path.Combine(Path.GetTempPath(), $"asr-deep-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tempAudioPath, new byte[] { 1, 2, 3, 4 });
        var disposed = false;
        try
        {
            var handler = new StubHandler(_ => Json("{\"text\":\"from video\",\"durationSeconds\":12}"));
            using var httpClient = new HttpClient(handler);

            var video = new Mock<IVideoAudioExtractionService>();
            video.Setup(x => x.IsVideoFileSupported("clip.mp4", "video/mp4")).Returns(true);
            video.Setup(x => x.IsVideoFileSupported(It.Is<string>(s => s != "clip.mp4"), It.IsAny<string>())).Returns(false);
            video.Setup(x => x.ExtractAudioToTempFileAsync(It.IsAny<Stream>(), "clip.mp4", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TransientAudioExtractionResult(
                    tempAudioPath,
                    4,
                    "audio/wav",
                    () => { disposed = true; return ValueTask.CompletedTask; }));

            var service = CreateService(httpClient, LocalProviderSection,
                videoService: video.Object,
                localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" });

            await using var content = new MemoryStream(new byte[64]);
            var result = await service.TranscribeAudioWithDurationAsync(content, "clip.mp4", "video/mp4");

            result.Text.Should().Be("from video");
            result.DurationSeconds.Should().Be(12);
            disposed.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempAudioPath))
            {
                File.Delete(tempAudioPath);
            }
        }
    }

    [TestMethod]
    public async Task TranscribeLocalAsr_ParsesTextAndDuration()
    {
        var handler = new StubHandler(_ => Json("{\"text\":\"local transcript\",\"durationSeconds\":7,\"modelRef\":\"whisper\"}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80/" });

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        result.Text.Should().Be("local transcript");
        result.DurationSeconds.Should().Be(7);
    }

    [TestMethod]
    public async Task TranscribeLocalAsr_Throws_OnErrorStatus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream down", Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" });

        await using var content = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Local ASR API failed*");
    }

    [TestMethod]
    public async Task TranscribeLocalAsr_WrapsTimeout_AsTimeoutException()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" });

        await using var content = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*timed out*");
    }

    [TestMethod]
    public async Task TranscribeGoogleGemini_ParsesCandidates()
    {
        var handler = new StubHandler(_ => Json(
            "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hello\"},{\"text\":\"world\"}]}}]}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, GoogleGeminiProviderSection,
            configurationValues: new Dictionary<string, string?> { ["GoogleGeminiApi:ApiKey"] = "gkey" },
            modelId: "gemini-2.5-flash");

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        result.Text.Should().Be("hello world");
    }

    [TestMethod]
    public async Task TranscribeGoogleGemini_Throws_WhenModelIdMissing()
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, GoogleGeminiProviderSection,
            configurationValues: new Dictionary<string, string?> { ["GoogleGeminiApi:ApiKey"] = "gkey" });

        await using var content = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
    }

    [TestMethod]
    public async Task TranscribeGoogleGemini_Throws_WhenApiKeyMissing()
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, GoogleGeminiProviderSection, modelId: "gemini-2.5-flash");

        await using var content = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GoogleGeminiApi:ApiKey is required*");
    }

    [TestMethod]
    public async Task TranscribeHuggingFace_Plain_ParsesText()
    {
        var handler = new StubHandler(_ => Json("{\"text\":\"hf transcript\"}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, HuggingFaceProviderSection,
            configurationValues: new Dictionary<string, string?> { ["HuggingFace:Token"] = "hf-token" },
            modelId: "openai/whisper-large-v3");

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        result.Text.Should().Be("hf transcript");
    }

    [TestMethod]
    public async Task TranscribeHuggingFace_WithTimestamps_BuildsTimestampedText()
    {
        var handler = new StubHandler(_ => Json(
            "{\"text\":\"full\",\"chunks\":[{\"text\":\"hello\",\"timestamp\":[0.0,1.0]},{\"text\":\"world\",\"timestamp\":[1.0,2.0]}]}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, HuggingFaceProviderSection,
            configurationValues: new Dictionary<string, string?> { ["HuggingFace:Token"] = "hf-token" },
            modelId: "openai/whisper-large-v3",
            requestPresetJson: "{\"ReturnTimestamps\":true}");

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        result.Text.Should().Contain("hello").And.Contain("world");
        result.Text.Should().Contain("[0.0-1.0]").And.Contain("[1.0-2.0]");
    }

    [TestMethod]
    public async Task TranscribeHuggingFace_Throws_WhenTokenMissing()
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, HuggingFaceProviderSection, modelId: "openai/whisper-large-v3");

        await using var content = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*HuggingFace:Token is required*");
    }

    [TestMethod]
    public async Task TranscribeOpenRouter_ParsesText_AndSucceeds()
    {
        var handler = new StubHandler(_ => Json("{\"text\":\"openrouter transcript\"}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, OpenRouterProviderSection,
            configurationValues: new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:AppTitle"] = "GuideAnts"
            },
            modelId: "openai/whisper-1");

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav");

        result.Text.Should().Be("openrouter transcript");
    }

    [TestMethod]
    public async Task TranscribeAzure_FormatsDiarizedTranscript_ForMultipleSpeakers()
    {
        var handler = new StubHandler(_ => Json(
            "{\"durationMilliseconds\":4000,\"phrases\":[" +
            "{\"offsetMilliseconds\":0,\"text\":\"hi there\",\"speaker\":0}," +
            "{\"offsetMilliseconds\":1000,\"text\":\"hello back\",\"speaker\":1}," +
            "{\"offsetMilliseconds\":2000,\"text\":\"again\",\"speaker\":0}]}"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, AzureProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://speech.example.com", ApiKey = "k" });

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav", enableDiarization: true);

        result.Text.Should().Contain("**Speaker 1:** hi there");
        result.Text.Should().Contain("**Speaker 2:** hello back");
        result.Text.Should().Contain("**Speaker 1:** again");
        result.DurationSeconds.Should().Be(4);
    }

    [TestMethod]
    public async Task TranscribeAzure_RetriesTransientStatus_ThenSucceeds()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("slow down", Encoding.UTF8, "text/plain")
                }
                : Json("{\"durationMilliseconds\":1000,\"phrases\":[{\"offsetMilliseconds\":0,\"text\":\"ok\",\"speaker\":0}]}");
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, AzureProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://speech.example.com", ApiKey = "k" },
            transcriptionOptions: new SpeechTranscriptionOptions { TimeoutSeconds = 120, MaxRetries = 2 });

        await using var content = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(content, "rec.wav", "audio/wav", enableDiarization: false);

        attempts.Should().Be(2);
        result.Text.Should().Be("ok");
    }

    private static SpeechTranscriptionService CreateService(
        HttpClient httpClient,
        string providerSection,
        IVideoAudioExtractionService? videoService = null,
        AzureSpeechServiceOptions? azureOptions = null,
        SpeechTranscriptionOptions? transcriptionOptions = null,
        LocalServiceHostsOptions? localServiceHostsOptions = null,
        IDictionary<string, string?>? configurationValues = null,
        string? modelId = null,
        string? requestPresetJson = null,
        int maxFileSizeMB = 500)
    {
        var speechOptionsMonitor = new StaticOptionsMonitor<AzureSpeechServiceOptions>(
            azureOptions ?? new AzureSpeechServiceOptions { Endpoint = "https://speech.example.com", ApiKey = "k" });
        var transcriptionOptionsMonitor = new StaticOptionsMonitor<SpeechTranscriptionOptions>(
            transcriptionOptions ?? new SpeechTranscriptionOptions { TimeoutSeconds = 120 });
        var localServiceHostsOptionsMonitor = new StaticOptionsMonitor<LocalServiceHostsOptions>(
            localServiceHostsOptions ?? new LocalServiceHostsOptions());
        var extractionOptionsMonitor = new StaticOptionsMonitor<MarkdownExtractionOptions>(new MarkdownExtractionOptions
        {
            MaxFileSizeMB = maxFileSizeMB,
            SupportedExtensions = [".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".webm", ".opus"]
        });

        IVideoAudioExtractionService resolvedVideoService;
        if (videoService is not null)
        {
            resolvedVideoService = videoService;
        }
        else
        {
            var defaultVideo = new Mock<IVideoAudioExtractionService>();
            defaultVideo.Setup(x => x.IsVideoFileSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
            resolvedVideoService = defaultVideo.Object;
        }

        var resolver = modelId is null
            ? new FakeServiceModeResolver(RoutedServiceNames.SpeechTranscription, providerSection: providerSection)
            : new FakeServiceModeResolver(
                (RoutedServiceNames.SpeechTranscription, new ServiceMode(
                    ModeId: "default",
                    ProviderSection: providerSection,
                    ModelId: modelId,
                    RequestPresetJson: requestPresetJson,
                    Enabled: true,
                    IsDefault: true)));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        return new SpeechTranscriptionService(
            httpClient,
            speechOptionsMonitor,
            transcriptionOptionsMonitor,
            localServiceHostsOptionsMonitor,
            extractionOptionsMonitor,
            resolvedVideoService,
            resolver,
            configuration,
            NullLogger<SpeechTranscriptionService>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
