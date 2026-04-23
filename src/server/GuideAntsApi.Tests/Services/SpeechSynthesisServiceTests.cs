using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SpeechSynthesisServiceTests
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechSynthesisBaseUrl";

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

    private static SpeechSynthesisService CreateService(
        HttpClient httpClient,
        string providerSection,
        SpeechSynthesisOptions synthesisOptions,
        LocalServiceHostsOptions localServiceHostsOptions)
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

        var resolver = new FakeServiceModeResolver(
            RoutedServiceNames.SpeechSynthesis,
            providerSection: providerSection);

        return new SpeechSynthesisService(
            httpClient,
            azureOptionsMonitor.Object,
            synthesisOptionsMonitor.Object,
            localServiceHostsOptionsMonitor.Object,
            resolver,
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
