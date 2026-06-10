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

namespace GuideAntsApi.Tests.Services.Components;

/// <summary>
/// Provider/branch coverage for <see cref="SpeechTranscriptionService"/> complementing the
/// base happy-path suite: validation guards, top-level exception wrapping, the OpenAI provider,
/// OpenRouter payload guards, and Azure/plain formatting edge cases.
/// </summary>
[TestClass]
public sealed class SpeechTranscriptionServiceTests
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechTranscriptionBaseUrl";
    private const string OpenRouterProviderSection = "OpenRouter";
    private const string OpenAiProviderSection = "OpenAI";

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_Throws_ForUnsupportedFileType()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" });

        await using var audio = new MemoryStream(new byte[16]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "notes.txt", "text/plain");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Unsupported audio/video file type*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_Throws_WhenFileTooLarge()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" },
            maxFileSizeMB: 0);

        await using var audio = new MemoryStream(new byte[1024]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*too large*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_WrapsHttpRequestException_AsInvalidOperation()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => throw new HttpRequestException("network down")));
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" });

        await using var audio = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Failed to transcribe audio*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_WrapsJsonException_AsParseFailure()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is not json", Encoding.UTF8, "application/json")
            }));
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechTranscriptionBaseUrl = "http://asr:80" });

        await using var audio = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Failed to parse transcription response*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_Local_Throws_WhenBaseUrlMissing()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, LocalProviderSection,
            localServiceHostsOptions: new LocalServiceHostsOptions());

        await using var audio = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SpeechTranscriptionBaseUrl is required*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_ThrowsRoutingException_ForUnsupportedProviderSection()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, "BogusProvider");

        await using var audio = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_Azure_ReturnsEmpty_WhenNoPhrases()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"durationMilliseconds\":0,\"phrases\":[]}", Encoding.UTF8, "application/json")
            }));
        var service = CreateService(httpClient, AzureProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://speech.example.com", ApiKey = "k" });

        await using var audio = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        result.Text.Should().BeEmpty();
        result.DurationSeconds.Should().Be(0);
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_Azure_FormatsPlainTranscription_WhenDiarizationDisabled()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"durationMilliseconds\":2000,\"phrases\":[{\"offsetMilliseconds\":0,\"text\":\"hello\",\"speaker\":0},{\"offsetMilliseconds\":1000,\"text\":\"world\",\"speaker\":1}]}",
                    Encoding.UTF8,
                    "application/json")
            }));
        var service = CreateService(httpClient, AzureProviderSection,
            azureOptions: new AzureSpeechServiceOptions { Endpoint = "https://speech.example.com", ApiKey = "k" });

        await using var audio = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav", enableDiarization: false);

        result.Text.Should().Be("hello world");
        result.Text.Should().NotContain("Speaker");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_OpenAi_ParsesJsonTranscript()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hello openai\"}", Encoding.UTF8, "application/json")
            });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, OpenAiProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "sk-test" },
            modelId: "whisper-1");

        await using var audio = new MemoryStream(new byte[64]);
        var result = await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        result.Text.Should().Be("hello openai");
        handler.LastRequestUri!.ToString().Should().Be("https://api.openai.com/v1/audio/transcriptions");
        handler.LastRequestHeaders.Should().ContainKey("Authorization");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_OpenAi_Throws_WhenApiKeyMissing()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, OpenAiProviderSection, modelId: "whisper-1");

        await using var audio = new MemoryStream(new byte[64]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*OpenAI:ApiKey is required*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_OpenRouter_Throws_WhenPayloadExceedsLimit()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, OpenRouterProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "or-key" },
            modelId: "openai/whisper-1",
            requestPresetJson: "{\"MaxAudioBytes\":10}");

        await using var audio = new MemoryStream(new byte[1024]);
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.wav", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds configured limit*");
    }

    [TestMethod]
    public async Task TranscribeAudioWithDurationAsync_OpenRouter_Throws_ForUnsupportedAudioFormat()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, OpenRouterProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "or-key" },
            modelId: "openai/whisper-1");

        await using var audio = new MemoryStream(new byte[64]);
        // Supported by content-type gate (audio/wav) but an unsupported OpenRouter format extension.
        var act = async () => await service.TranscribeAudioWithDurationAsync(audio, "rec.aiff", "audio/wav");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not support audio format*");
    }

    private static SpeechTranscriptionService CreateService(
        HttpClient httpClient,
        string providerSection,
        AzureSpeechServiceOptions? azureOptions = null,
        SpeechTranscriptionOptions? transcriptionOptions = null,
        LocalServiceHostsOptions? localServiceHostsOptions = null,
        IDictionary<string, string?>? configurationValues = null,
        string? modelId = null,
        string? requestPresetJson = null,
        int maxFileSizeMB = 500)
    {
        var speechOptionsMonitor = new Mock<IOptionsMonitor<AzureSpeechServiceOptions>>();
        speechOptionsMonitor.SetupGet(x => x.CurrentValue)
            .Returns(azureOptions ?? new AzureSpeechServiceOptions { Endpoint = "https://speech.example.com", ApiKey = "k" });

        var transcriptionOptionsMonitor = new Mock<IOptionsMonitor<SpeechTranscriptionOptions>>();
        transcriptionOptionsMonitor.SetupGet(x => x.CurrentValue)
            .Returns(transcriptionOptions ?? new SpeechTranscriptionOptions { TimeoutSeconds = 120 });

        var localServiceHostsOptionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        localServiceHostsOptionsMonitor.SetupGet(x => x.CurrentValue)
            .Returns(localServiceHostsOptions ?? new LocalServiceHostsOptions());

        var extractionOptionsMonitor = new Mock<IOptionsMonitor<MarkdownExtractionOptions>>();
        extractionOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new MarkdownExtractionOptions
        {
            MaxFileSizeMB = maxFileSizeMB,
            SupportedExtensions = [".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".webm", ".opus"]
        });

        var videoService = new Mock<IVideoAudioExtractionService>();
        videoService.Setup(x => x.IsVideoFileSupported(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

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
            speechOptionsMonitor.Object,
            transcriptionOptionsMonitor.Object,
            localServiceHostsOptionsMonitor.Object,
            extractionOptionsMonitor.Object,
            videoService.Object,
            resolver,
            configuration,
            NullLogger<SpeechTranscriptionService>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
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

            return Task.FromResult(responder(request));
        }
    }
}
