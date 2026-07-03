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
            result.ProviderId.Should().Be(ServiceProviderIds.SpeechSynthesisLocalTtsHttp);
            result.DurationSeconds.Should().Be(5);
            File.Exists(outputPath).Should().BeTrue();
            File.ReadAllBytes(outputPath).Should().Equal(wavBytes);
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("http://guideants-ai/tts/synthesize");
            handler.LastRequestHeaders.Should().ContainKey("x-request-id");
            handler.LastRequestBody.Should().Contain("\"text\":\"Hello world\"");
            handler.LastRequestBody.Should().Contain("\"speed\":1");
            // Wire contract (RULES I5): no lang_code from .NET — the engine
            // derives it from the active catalog entry / voice pack.
            handler.LastRequestBody.Should().NotContain("lang_code");
            // No voice configured for this mode, so the field is omitted and the
            // engine uses the active model's default voice (no hardcoded default).
            handler.LastRequestBody.Should().NotContain("\"voice\"");
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
            result.ProviderId.Should().Be(ServiceProviderIds.SpeechSynthesisGoogleTextToSpeech);
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
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return BuildHuggingFaceProviderMappingResponse(provider: "hf-inference", providerId: "hf-tts-model");
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wavBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            response.Headers.Add("x-audio-duration-seconds", "3.6");
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
            },
            modelId: "hf-tts-model");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-hf-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello HF</speak>", outputPath);

            result.Success.Should().BeTrue();
            result.DurationSeconds.Should().Be(4);
            handler.RequestUris.Should().Contain(uri => uri.Contains("https://huggingface.co/api/models/hf-tts-model?expand[]=inferenceProviderMapping", StringComparison.Ordinal));
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("https://router.huggingface.co/hf-inference/models/hf-tts-model");
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
    public async Task SynthesizeToWavAsync_HuggingFace_ReturnsFailureWithHttpStatusAndBody_OnRateLimit()
    {
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return BuildHuggingFaceProviderMappingResponse(provider: "hf-inference", providerId: "hf-tts-model");
            }

            return new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":\"rate limited\"}", Encoding.UTF8, "application/json")
            };
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
            },
            modelId: "hf-tts-model");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-hf-rate-limit-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello HF</speak>", outputPath);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Hugging Face TTS failed");
            result.ErrorMessage.Should().Contain("429");
            result.ErrorMessage.Should().Contain("rate limited");
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
    public async Task SynthesizeToWavAsync_HuggingFace_FailsOnUnexpectedContentType()
    {
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return BuildHuggingFaceProviderMappingResponse(provider: "hf-inference", providerId: "hf-tts-model");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"error\":\"not-audio\"}", Encoding.UTF8, "application/json")
            };
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
            },
            modelId: "hf-tts-model");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-hf-bad-content-type-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello HF</speak>", outputPath);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("unexpected content-type");
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
    public async Task SynthesizeToWavAsync_HuggingFaceReplicate_UsesPredictionsRouteAndDownloadsAudio()
    {
        var wavBytes = Encoding.UTF8.GetBytes("fake-replicate-wav");
        const string expectedAudioUrl = "https://replicate.delivery/mock/output.wav";
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return BuildHuggingFaceProviderMappingResponse(provider: "replicate", providerId: "resemble-ai/chatterbox-turbo");
            }

            if (request.RequestUri?.ToString() == "https://router.huggingface.co/replicate/v1/models/resemble-ai/chatterbox-turbo/predictions")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"status":"succeeded","output":"{{expectedAudioUrl}}"}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.RequestUri?.ToString() == expectedAudioUrl)
            {
                var download = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(wavBytes)
                };
                download.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                return download;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected request")
            };
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
                ["HuggingFace:RouterBaseUrl"] = "https://router.huggingface.co/v1",
            },
            modelId: "ResembleAI/chatterbox");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-hf-replicate-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello HF</speak>", outputPath);

            result.Success.Should().BeTrue();
            File.ReadAllBytes(outputPath).Should().Equal(wavBytes);
            handler.RequestUris.Should().Contain("https://router.huggingface.co/replicate/v1/models/resemble-ai/chatterbox-turbo/predictions");
            handler.RequestUris.Should().Contain(expectedAudioUrl);
            var replicateRequest = handler.CapturedRequests
                .FirstOrDefault(entry => string.Equals(
                    entry.Uri,
                    "https://router.huggingface.co/replicate/v1/models/resemble-ai/chatterbox-turbo/predictions",
                    StringComparison.Ordinal));
            replicateRequest.Should().NotBeNull();
            replicateRequest!.Body.Should().Contain("\"input\":{\"text\":\"Hello HF\"}");
            replicateRequest.Headers.Should().ContainKey("Prefer");
            replicateRequest.Headers["Prefer"].Should().Contain("wait");
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
        var pcmBytes = new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00 };
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pcmBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/pcm");
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
            },
            modelId: "hexgrad/kokoro-82m");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tts-or-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await service.SynthesizeToWavAsync("<speak>Hello OR</speak>", outputPath);

            result.Success.Should().BeTrue();
            handler.LastRequestUri.Should().NotBeNull();
            handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/audio/speech");
            handler.LastRequestBody.Should().Contain("\"model\":\"hexgrad/kokoro-82m\"");
            handler.LastRequestBody.Should().Contain("\"voice\":\"af_alloy\"");
            handler.LastRequestBody.Should().Contain("\"response_format\":\"pcm\"");

            var outputBytes = await File.ReadAllBytesAsync(outputPath);
            outputBytes.Length.Should().BeGreaterThan(pcmBytes.Length);
            Encoding.ASCII.GetString(outputBytes, 0, 4).Should().Be("RIFF");
            Encoding.ASCII.GetString(outputBytes, 8, 4).Should().Be("WAVE");
            outputBytes[^pcmBytes.Length..].Should().Equal(pcmBytes);
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

    private static HttpResponseMessage BuildHuggingFaceProviderMappingResponse(string provider, string providerId)
    {
        var payload = $$"""
        {
          "inferenceProviderMapping": {
            "{{provider}}": {
              "status": "live",
              "providerId": "{{providerId}}",
              "task": "text-to-speech"
            }
          }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public sealed record CapturedRequest(string Uri, string Body, Dictionary<string, string> Headers);

        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public Dictionary<string, string> LastRequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RequestUris { get; } = [];
        public List<CapturedRequest> CapturedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.RequestUri != null)
            {
                RequestUris.Add(request.RequestUri.ToString());
            }
            LastRequestHeaders.Clear();
            foreach (var header in request.Headers)
            {
                LastRequestHeaders[header.Key] = string.Join(",", header.Value);
            }

            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri != null)
            {
                CapturedRequests.Add(new CapturedRequest(
                    request.RequestUri.ToString(),
                    LastRequestBody,
                    new Dictionary<string, string>(LastRequestHeaders, StringComparer.OrdinalIgnoreCase)));
            }

            return _responder(request);
        }
    }
}
