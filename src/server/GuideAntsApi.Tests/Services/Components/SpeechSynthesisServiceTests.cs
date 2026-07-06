using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.Components;

/// <summary>
/// Provider/validation branch coverage for <see cref="SpeechSynthesisService"/>
/// beyond the happy paths exercised in the sibling test class under the parent namespace.
/// </summary>
[TestClass]
public sealed class SpeechSynthesisServiceTests
{
    private const string GoogleProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";
    private const string OpenAiProviderSection = "OpenAI";

    [TestMethod]
    public async Task SynthesizeToWavAsync_Throws_WhenSsmlEmpty()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, OpenAiProviderSection);

        var act = async () => await service.SynthesizeToWavAsync("   ", TempOutputPath());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenAi_Success()
    {
        var audio = Encoding.UTF8.GetBytes("openai-audio");
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            OpenAiProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "sk-key" },
            modelId: "gpt-4o-mini-tts",
            requestPresetJson: "{\"VoiceName\":\"alloy\"}");

        var outputPath = TempOutputPath();
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello OpenAI</speak>", outputPath);

            result.Success.Should().BeTrue();
            result.ProviderId.Should().Be(ServiceProviderIds.SpeechSynthesisOpenAiTts);
            handler.LastRequestUri!.ToString().Should().Be("https://api.openai.com/v1/audio/speech");
            handler.LastRequestBody.Should().Contain("\"voice\":\"alloy\"");
            File.ReadAllBytes(outputPath).Should().Equal(audio);
        }
        finally
        {
            SafeDelete(outputPath);
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenAi_Fails_WhenModelIdMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, OpenAiProviderSection); // single-service ctor -> ModelId null

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("OpenAI TTS requires mode.ModelId");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenAi_Fails_WhenVoiceNameMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(
            httpClient,
            OpenAiProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "sk-key" },
            modelId: "gpt-4o-mini-tts");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("requires VoiceName");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenAi_Fails_WhenApiKeyMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(
            httpClient,
            OpenAiProviderSection,
            modelId: "gpt-4o-mini-tts",
            requestPresetJson: "{\"VoiceName\":\"alloy\"}");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("OpenAI:ApiKey is required");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenAi_Fails_WhenApiReturnsError()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad request", Encoding.UTF8, "text/plain")
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            OpenAiProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "sk-key" },
            modelId: "gpt-4o-mini-tts",
            requestPresetJson: "{\"VoiceName\":\"alloy\"}");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("OpenAI TTS failed: 400");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenRouter_Fails_WhenApiKeyMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, OpenRouterProviderSection, modelId: "hexgrad/kokoro-82m");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("OpenRouter:ApiKey is required");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenRouter_Fails_WhenJsonReturnedInsteadOfAudio()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"nope\"}", Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            OpenRouterProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "or-key" },
            modelId: "hexgrad/kokoro-82m");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("returned JSON instead of audio");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenRouter_Fails_WhenMp3Returned()
    {
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("mp3-bytes"))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            OpenRouterProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "or-key" },
            modelId: "hexgrad/kokoro-82m");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("returned MP3 audio");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_OpenRouter_WrapsRawPcm_AsWav()
    {
        var pcm = new byte[] { 0x01, 0x00, 0x02, 0x00 };
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/pcm");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            OpenRouterProviderSection,
            configurationValues: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "or-key" },
            modelId: "hexgrad/kokoro-82m");

        var outputPath = TempOutputPath();
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", outputPath);

            result.Success.Should().BeTrue();
            var bytes = await File.ReadAllBytesAsync(outputPath);
            Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
            bytes[^pcm.Length..].Should().Equal(pcm);
        }
        finally
        {
            SafeDelete(outputPath);
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_Google_Fails_WhenModelIdMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, GoogleProviderSection); // ModelId null

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("requires mode.ModelId");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_Google_Fails_WhenVoiceNameMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, GoogleProviderSection, modelId: "gemini-2.5-flash-preview-tts");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("requires VoiceName");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_Google_WrapsRawPcm_WhenNonWavMimeType()
    {
        var pcm = new byte[] { 0x05, 0x00, 0x06, 0x00 };
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/L16;rate=24000\",\"data\":\""
                    + Convert.ToBase64String(pcm) + "\"}}]}}]}",
                Encoding.UTF8,
                "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            GoogleProviderSection,
            configurationValues: new Dictionary<string, string?> { ["GoogleGeminiApi:ApiKey"] = "gemini-key" },
            modelId: "gemini-2.5-flash-preview-tts",
            requestPresetJson: "{\"VoiceName\":\"Kore\"}");

        var outputPath = TempOutputPath();
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", outputPath);

            result.Success.Should().BeTrue();
            var bytes = await File.ReadAllBytesAsync(outputPath);
            Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
            bytes[^pcm.Length..].Should().Equal(pcm);
        }
        finally
        {
            SafeDelete(outputPath);
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_Google_Fails_WhenNoAudioReturned()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"no audio\"}]}}]}", Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            GoogleProviderSection,
            configurationValues: new Dictionary<string, string?> { ["GoogleGeminiApi:ApiKey"] = "gemini-key" },
            modelId: "gemini-2.5-flash-preview-tts",
            requestPresetJson: "{\"VoiceName\":\"Kore\"}");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("returned no audio");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_HuggingFace_Fails_WhenTokenMissing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(httpClient, HuggingFaceProviderSection, modelId: "some/model");

        var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HuggingFace:Token is required");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_HuggingFaceFalAi_DownloadsAudioFromUrl()
    {
        var audio = Encoding.UTF8.GetBytes("fal-audio");
        const string audioUrl = "https://fal.media/output.wav";
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"inferenceProviderMapping\":{\"fal-ai\":{\"status\":\"live\",\"providerId\":\"fal-model\",\"task\":\"text-to-speech\"}}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.RequestUri?.ToString() == audioUrl)
            {
                var download = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
                download.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                return download;
            }

            // fal-ai inference call returns JSON with audio url.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"audio\":{{\"url\":\"{audioUrl}\"}}}}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            HuggingFaceProviderSection,
            configurationValues: new Dictionary<string, string?> { ["HuggingFace:Token"] = "hf-token" },
            modelId: "some/fal-model");

        var outputPath = TempOutputPath();
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hi</speak>", outputPath);

            result.Success.Should().BeTrue();
            File.ReadAllBytes(outputPath).Should().Equal(audio);
        }
        finally
        {
            SafeDelete(outputPath);
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_Local_Fails_WhenTextEmptyAfterStripping()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(
            httpClient,
            "LocalServiceHosts:SpeechSynthesisBaseUrl",
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechSynthesisBaseUrl = "http://guideants-ai:80" });

        // SSML strips to empty -> service throws internally and reports failure.
        var result = await service.SynthesizeToWavAsync("<speak><break/></speak>", TempOutputPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty after SSML stripping");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_Local_ForwardsVoiceWithoutLangCode()
    {
        var audio = Encoding.ASCII.GetBytes("RIFFfakeWAVE");
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(audio)
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            "LocalServiceHosts:SpeechSynthesisBaseUrl",
            localServiceHostsOptions: new LocalServiceHostsOptions { SpeechSynthesisBaseUrl = "http://guideants-ai:80" },
            configurationValues: new Dictionary<string, string?> { ["SpeechSynthesis:Speed"] = "1.25" },
            modelId: "chatterbox",
            requestPresetJson: "{\"VoiceName\":\"en_gb_cv_002\",\"LanguageCode\":\"z\",\"Speed\":\"0.5\"}");
        var outputPath = TempOutputPath();

        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello Chatterbox</speak>", outputPath);

            result.Success.Should().BeTrue();
            using var document = JsonDocument.Parse(handler.LastRequestBody);
            var root = document.RootElement;

            // The configured voice is forwarded verbatim; the engine resolves
            // its meaning per the active family. lang_code is NOT sent from
            // .NET (RULES I5) — no hardcoded language map remains.
            root.GetProperty("voice").GetString().Should().Be("en_gb_cv_002");
            root.TryGetProperty("lang_code", out _).Should().BeFalse();
            root.GetProperty("speed").GetDouble().Should().BeApproximately(1.25, 0.001);
        }
        finally
        {
            SafeDelete(outputPath);
        }
    }

    private static string TempOutputPath() =>
        Path.Combine(Path.GetTempPath(), "tts-branch-" + Guid.NewGuid().ToString("N"), "out.wav");

    private static void SafeDelete(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static SpeechSynthesisService CreateService(
        HttpClient httpClient,
        string providerSection,
        LocalServiceHostsOptions? localServiceHostsOptions = null,
        IDictionary<string, string?>? configurationValues = null,
        string? modelId = null,
        string? requestPresetJson = null)
    {
        var azureOptionsMonitor = new Mock<IOptionsMonitor<AzureSpeechServiceOptions>>();
        azureOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new AzureSpeechServiceOptions
        {
            ApiKey = "test-key",
            Region = "eastus",
            Endpoint = "https://speech.example.com"
        });

        var synthesisOptionsMonitor = new Mock<IOptionsMonitor<SpeechSynthesisOptions>>();
        synthesisOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new SpeechSynthesisOptions());

        var localServiceHostsOptionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        localServiceHostsOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(localServiceHostsOptions ?? new LocalServiceHostsOptions());

        var resolver = modelId is null
            ? new FakeServiceModeResolver(RoutedServiceNames.SpeechSynthesis, providerSection: providerSection)
            : new FakeServiceModeResolver(
                (RoutedServiceNames.SpeechSynthesis, new ServiceMode(
                    ModeId: "default",
                    ProviderSection: providerSection,
                    ModelId: modelId,
                    RequestPresetJson: requestPresetJson,
                    Enabled: true,
                    IsDefault: true)));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        return new SpeechSynthesisService(
            httpClient,
            azureOptionsMonitor.Object,
            synthesisOptionsMonitor.Object,
            localServiceHostsOptionsMonitor.Object,
            resolver,
            configuration,
            NullLogger<SpeechSynthesisService>.Instance);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
