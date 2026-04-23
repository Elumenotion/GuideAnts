using FluentAssertions;
using GuideAntsApi.Services;
using HtmlAgility;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class WebScrapingServiceTests
{
    private static string LongMarkdown => new('a', 140);

    [TestMethod]
    public async Task GetPageContentAsync_InvalidUrl_ThrowsArgumentException()
    {
        var excludedHostService = CreateExcludedHostMock();
        var browserRenderingClient = CreateBrowserRenderingClientMock();
        var service = CreateService(excludedHostService.Object, browserRenderingClient.Object);

        var act = async () => await service.GetPageContentAsync("ftp://example.com");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task GetPageContentAsync_ExcludedHost_ShortCircuitsWithoutExtraction()
    {
        var excludedHostService = CreateExcludedHostMock();
        var browserRenderingClient = CreateBrowserRenderingClientMock();
        excludedHostService
            .Setup(x => x.GetExcludedHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" });
        excludedHostService
            .Setup(x => x.IsHostExcluded("example.com", It.IsAny<IReadOnlySet<string>>()))
            .Returns(true);

        var htmlCalled = false;
        var browserCalled = false;

        browserRenderingClient
            .Setup(x => x.RenderHtmlAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                browserCalled = true;
                return new BrowserRenderedPageResult(true, "<html></html>", null, null, 200);
            });

        var service = CreateService(
            excludedHostService.Object,
            browserRenderingClient.Object,
            _ =>
            {
                htmlCalled = true;
                return Task.FromResult(new MarkdownConversionResult { Content = LongMarkdown });
            });

        var result = await service.GetPageContentAsync("https://example.com/path");

        result.Should().Be("Host is excluded from reading: example.com");
        htmlCalled.Should().BeFalse();
        browserCalled.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetPageContentAsync_UsesHtmlAgility_WhenReadable()
    {
        var excludedHostService = CreateExcludedHostMock();
        var browserRenderingClient = CreateBrowserRenderingClientMock();
        var browserCalled = false;

        browserRenderingClient
            .Setup(x => x.RenderHtmlAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                browserCalled = true;
                return new BrowserRenderedPageResult(true, "<html></html>", null, null, 200);
            });

        var service = CreateService(
            excludedHostService.Object,
            browserRenderingClient.Object,
            _ => Task.FromResult(new MarkdownConversionResult { Content = LongMarkdown }));

        var result = await service.GetPageContentAsync("https://example.com/path");

        result.Should().Be(LongMarkdown);
        browserCalled.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetPageContentAsync_FallsBackToBrowserRender_WhenHtmlAgilityUnreadable()
    {
        var excludedHostService = CreateExcludedHostMock();
        var browserRenderingClient = CreateBrowserRenderingClientMock();
        browserRenderingClient
            .Setup(x => x.RenderHtmlAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserRenderedPageResult(true, "<html><body>" + LongMarkdown + "</body></html>", null, "https://example.com/final", 200));

        var service = CreateService(
            excludedHostService.Object,
            browserRenderingClient.Object,
            _ => Task.FromResult(new MarkdownConversionResult { Content = "Error: failed" }));

        var result = await service.GetPageContentAsync("https://example.com/path");

        result.Should().Be(LongMarkdown);
    }

    [TestMethod]
    public async Task GetPageContentAsync_AddsExcludedHost_On403()
    {
        var excludedHostService = CreateExcludedHostMock();
        var browserRenderingClient = CreateBrowserRenderingClientMock();
        browserRenderingClient
            .Setup(x => x.RenderHtmlAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserRenderedPageResult(false, null, "HTTP 403", "https://blocked.example.com/page", 403));

        var service = CreateService(
            excludedHostService.Object,
            browserRenderingClient.Object,
            _ => Task.FromResult(new MarkdownConversionResult { Content = "Error: failed" }));

        var result = await service.GetPageContentAsync("https://example.com/path");

        result.Should().Be("Page was not readable: HTTP 403");
        excludedHostService.Verify(
            x => x.TryAddExcludedHostAsync(
                "blocked.example.com",
                It.Is<string>(reason => reason.Contains("403")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task GetPageContentAsync_ReturnsDeterministicUnreadableMessage()
    {
        var excludedHostService = CreateExcludedHostMock();
        var browserRenderingClient = CreateBrowserRenderingClientMock();
        browserRenderingClient
            .Setup(x => x.RenderHtmlAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserRenderedPageResult(false, null, "Navigation timed out", null, null));

        var service = CreateService(
            excludedHostService.Object,
            browserRenderingClient.Object,
            _ => Task.FromResult(new MarkdownConversionResult { Content = "short" }));

        var result = await service.GetPageContentAsync("https://example.com/path");

        result.Should().Be("Page was not readable: Navigation timed out");
    }

    private static WebScrapingService CreateService(
        IExcludedHostService excludedHostService,
        IBrowserRenderingClient browserRenderingClient,
        Func<string, Task<MarkdownConversionResult>>? htmlAgilityFetch = null)
    {
        return new WebScrapingService(
            NullLogger<WebScrapingService>.Instance,
            excludedHostService,
            browserRenderingClient,
            htmlAgilityFetch);
    }

    private static Mock<IExcludedHostService> CreateExcludedHostMock()
    {
        var mock = new Mock<IExcludedHostService>();
        mock
            .Setup(x => x.GetExcludedHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        mock
            .Setup(x => x.IsHostExcluded(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>()))
            .Returns(false);
        mock
            .Setup(x => x.TryAddExcludedHostAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return mock;
    }

    private static Mock<IBrowserRenderingClient> CreateBrowserRenderingClientMock()
    {
        var mock = new Mock<IBrowserRenderingClient>();
        mock
            .Setup(x => x.RenderHtmlAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserRenderedPageResult(false, null, "Browser rendering was not configured for this test", null, null));

        return mock;
    }
}
