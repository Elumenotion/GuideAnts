using System.Net;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class WebScrapingEndpointsTests : BaseIntegrationTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task ScrapeUrl_WithValidUrl_ReturnsContent()
    {
        // Act
        var response = await Client!.GetAsync("/api/web-scraping/content?Url=https://example.com");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadAsStringAsync();
        result.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task ScrapeUrl_WithInvalidUrl_ReturnsBadRequest()
    {
        // Act
        var response = await Client!.GetAsync("/api/web-scraping/content?Url=not-a-url");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task ScrapeUrl_WithEmptyUrl_ReturnsBadRequest()
    {
        // Act
        var response = await Client!.GetAsync("/api/web-scraping/content?Url=");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task ScrapeUrl_WithNonExistentUrl_ReturnsNotFound()
    {
        // Act
        var response = await Client!.GetAsync("/api/web-scraping/content?Url=https://this-domain-does-not-exist-123456789.com");

        // Assert
        // Endpoint intentionally degrades to 200 with a readable message for non-readable pages.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Page was not readable");
    }
} 
