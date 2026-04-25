using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SpeechSynthesisServiceTests
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechSynthesisBaseUrl";
    private const string GoogleProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";

    [TestMethod]
    public async Task SynthesizeToWavAsync_UsesLocalTtsProvider_WhenModeSelectsLocal()
    {
        var wavBytes = Encoding.UTF8.GetBytes("fake-wav");
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wavBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            response.Headers.Add("x-audio-duration-seconds", "4.6");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: LocalProviderSection,
            new SpeechSynthesisOptions
            {
                TimeoutSeconds = 120
            },
            new LocalServiceHostsOptions
            {
                SpeechSynthesisBaseUrl = "http://guideants-ai:80"
            });

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak><voice>Hello <break/>world</voice></speak>", outputPath);

            result.Success.Should().BeTrue();
            result.DurationSeconds.Should().Be(5);
            File.Exists(outputPath).Should().BeTrue();
            File.ReadAllBytes(outputPath).Should().Equal(wavBytes);
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("http://guideants-ai/tts/synthesize");
            handler.LastRequestHeaders.Should().ContainKey("x-request-id");
            handler.LastRequestBody.Should().Contain("\"text\":\"Hello world\"");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_ThrowsRoutingException_WhenModeReferencesUnsupportedProviderSection()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = CreateService(
            httpClient,
            providerSection: "SomeBogusSection",
            new SpeechSynthesisOptions(),
            new LocalServiceHostsOptions());

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-{Guid.NewGuid():N}.wav");
        var act = async () => await service.SynthesizeToWavAsync("hello", outputPath);

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
        ex.Which.ProviderSection.Should().Be("SomeBogusSection");
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_UsesGoogleProviderWithTypedPayload()
    {
        var wavBytes = Encoding.UTF8.GetBytes("fake-google-wav");
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/wav\",\"data\":\"" + Convert.ToBase64String(wavBytes) + "\"}}]}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: GoogleProviderSection,
            synthesisOptions: new SpeechSynthesisOptions(),
            localServiceHostsOptions: new LocalServiceHostsOptions(),
            configurationValues: new Dictionary<string, string?>
            {
                ["GoogleGeminiApi:ApiKey"] = "gemini-key"
            },
            modelId: "gemini-2.5-flash-preview-tts",
            requestPresetJson: "{\"VoiceName\":\"Kore\"}");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-google-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello Google</speak>", outputPath);

            result.Success.Should().BeTrue();
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-preview-tts:generateContent");
            handler.LastRequestBody.Should().Contain("\"responseModalities\":[\"AUDIO\"]");
            handler.LastRequestBody.Should().Contain("\"voiceName\":\"Kore\"");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_UsesHuggingFaceProviderWithTypedPayload()
    {
        var wavBytes = Encoding.UTF8.GetBytes("fake-hf-wav");
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wavBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: HuggingFaceProviderSection,
            synthesisOptions: new SpeechSynthesisOptions(),
            localServiceHostsOptions: new LocalServiceHostsOptions(),
            configurationValues: new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token",
                ["HuggingFace:TtsAllowedModels"] = "hf-tts-model"
            },
            modelId: "hf-tts-model");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-hf-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello HF</speak>", outputPath);

            result.Success.Should().BeTrue();
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("https://api-inference.huggingface.co/models/hf-tts-model");
            handler.LastRequestBody.Should().Contain("\"inputs\":\"Hello HF\"");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [TestMethod]
    public async Task SynthesizeToWavAsync_UsesOpenRouterProviderWithTypedPayload()
    {
        var wavBytes = Encoding.UTF8.GetBytes("fake-openrouter-wav");
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wavBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: OpenRouterProviderSection,
            synthesisOptions: new SpeechSynthesisOptions(),
            localServiceHostsOptions: new LocalServiceHostsOptions(),
            configurationValues: new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
                ["OpenRouter:TtsAllowedModels"] = "or-tts-model"
            },
            modelId: "or-tts-model");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-or-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello OR</speak>", outputPath);

            result.Success.Should().BeTrue();
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/tts");
            handler.LastRequestBody.Should().Contain("\"model\":\"or-tts-model\"");
            handler.LastRequestBody.Should().Contain("\"voice\":\"alloy\"");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static SpeechSynthesisService CreateService(
        HttpClient httpClient,
        string providerSection,
        SpeechSynthesisOptions synthesisOptions,
        LocalServiceHostsOptions localServiceHostsOptions,
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
        synthesisOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(synthesisOptions);

        var localServiceHostsOptionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        localServiceHostsOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(localServiceHostsOptions);

        var resolver = modelId is null
            ? new FakeServiceModeResolver(
                RoutedServiceNames.SpeechSynthesis,
                providerSection: providerSection)
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
