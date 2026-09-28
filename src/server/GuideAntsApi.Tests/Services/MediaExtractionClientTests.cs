using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Http;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.DataModel.Media;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class MediaExtractionClientTests
{
    [TestMethod]
    public async Task ExtractAudioAsync_PostsJsonToConfiguredMediaBaseUrl()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"outputPath\":\".system/media-extract/abc/output.mp3\",\"contentType\":\"audio/mpeg\",\"fileSize\":321}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var optionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(new LocalServiceHostsOptions
        {
            MediaBaseUrl = "http://guideants-ai:80"
        });
        var videoOptionsMonitor = new Mock<IOptionsMonitor<VideoAudioExtractionOptions>>();
        videoOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new VideoAudioExtractionOptions
        {
            TimeoutSeconds = 1800
        });

        var client = new MediaExtractionClient(
            httpClient,
            optionsMonitor.Object,
            videoOptionsMonitor.Object,
            NullLogger<MediaExtractionClient>.Instance);

        var result = await client.ExtractAudioAsync(new MediaExtractionRequest
        {
            SourcePath = ".system/media-extract/abc/input.mp4",
            OutputPath = ".system/media-extract/abc/output.mp3"
        });

        result.OutputPath.Should().Be(".system/media-extract/abc/output.mp3");
        result.ContentType.Should().Be("audio/mpeg");
        result.FileSize.Should().Be(321);
        handler.LastRequestUri.Should().Be(new Uri("http://guideants-ai/media/extract-audio"));
        handler.LastRequestBody.Should().Contain("\"sourcePath\":\".system/media-extract/abc/input.mp4\"");
        handler.LastRequestBody.Should().Contain("\"outputPath\":\".system/media-extract/abc/output.mp3\"");
        handler.LastRequest!.Headers.TryGetValues(LocalServiceRequestHeaders.RequestTimeoutSeconds, out var timeoutValues)
            .Should().BeTrue();
        timeoutValues!.Single().Should().Be("1800");
    }

    [TestMethod]
    public async Task ExtractAudioAsync_SurfacesApiErrors()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"detail\":\"bad path\"}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var optionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(new LocalServiceHostsOptions
        {
            MediaBaseUrl = "http://guideants-ai:80"
        });
        var videoOptionsMonitor = new Mock<IOptionsMonitor<VideoAudioExtractionOptions>>();
        videoOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new VideoAudioExtractionOptions
        {
            TimeoutSeconds = 1800
        });

        var client = new MediaExtractionClient(
            httpClient,
            optionsMonitor.Object,
            videoOptionsMonitor.Object,
            NullLogger<MediaExtractionClient>.Instance);

        var act = async () => await client.ExtractAudioAsync(new MediaExtractionRequest
        {
            SourcePath = "../../outside.mp4",
            OutputPath = ".system/media-extract/abc/output.mp3"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bad path*");
    }

    [TestMethod]
    public async Task ExtractAudioAsync_IncludesStructuredErrorTypeInExceptionMessage()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)422)
            {
                Content = new StringContent(
                    "{\"detail\":\"Source file contains no audio stream: .system/media-extract/abc/input.mp4\",\"errorType\":\"NO_AUDIO_STREAM\"}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var optionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(new LocalServiceHostsOptions
        {
            MediaBaseUrl = "http://guideants-ai:80"
        });
        var videoOptionsMonitor = new Mock<IOptionsMonitor<VideoAudioExtractionOptions>>();
        videoOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new VideoAudioExtractionOptions
        {
            TimeoutSeconds = 1800
        });

        var client = new MediaExtractionClient(
            httpClient,
            optionsMonitor.Object,
            videoOptionsMonitor.Object,
            NullLogger<MediaExtractionClient>.Instance);

        var act = async () => await client.ExtractAudioAsync(new MediaExtractionRequest
        {
            SourcePath = ".system/media-extract/abc/input.mp4",
            OutputPath = ".system/media-extract/abc/output.mp3"
        });

        var exception = await act.Should().ThrowAsync<MediaNoAudioStreamException>();
        exception.WithMessage("Source file contains no audio stream: .system/media-extract/abc/input.mp4");
        exception.Which.SourcePath.Should().Be(".system/media-extract/abc/input.mp4");
    }

    [TestMethod]
    public async Task ExtractAudioAsync_ParsesErrorTypeFromNestedDetailObject()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)422)
            {
                Content = new StringContent(
                    "{\"detail\":{\"message\":\"Source file could not be probed: bad data\",\"errorType\":\"UNREADABLE_MEDIA\"}}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var optionsMonitor = new Mock<IOptionsMonitor<LocalServiceHostsOptions>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(new LocalServiceHostsOptions
        {
            MediaBaseUrl = "http://guideants-ai:80"
        });
        var videoOptionsMonitor = new Mock<IOptionsMonitor<VideoAudioExtractionOptions>>();
        videoOptionsMonitor.SetupGet(x => x.CurrentValue).Returns(new VideoAudioExtractionOptions
        {
            TimeoutSeconds = 1800
        });

        var client = new MediaExtractionClient(
            httpClient,
            optionsMonitor.Object,
            videoOptionsMonitor.Object,
            NullLogger<MediaExtractionClient>.Instance);

        var act = async () => await client.ExtractAudioAsync(new MediaExtractionRequest
        {
            SourcePath = ".system/media-extract/abc/input.mp4",
            OutputPath = ".system/media-extract/abc/output.mp3"
        });

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage(
            "Media extraction API failed (422): [UNREADABLE_MEDIA] Source file could not be probed: bad data");
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public Uri? LastRequestUri { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
