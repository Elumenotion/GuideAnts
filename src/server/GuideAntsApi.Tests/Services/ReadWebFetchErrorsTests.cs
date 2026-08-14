using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class ReadWebFetchErrorsTests
{
    [TestMethod]
    public void BuildMessage_WhenNotFound_ReturnsDoNotRetryGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(404, "HTTP 404", 404, "HTTP 404");

        message.Should().Be(
            "404 Not Found. This URL does not exist. Do not retry — fix the path or use local file search.");
    }

    [TestMethod]
    public void BuildMessage_WhenForbiddenDominatesNotFound_ReturnsForbiddenGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(403, "HTTP 403", 404, "HTTP 404");

        message.Should().Be(
            "403 Forbidden. This host blocks unauthenticated access. Do not retry — use a different source or local files.");
    }

    [TestMethod]
    public void BuildMessage_WhenTimedOut_ReturnsTimeoutGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(null, "Direct fetch timed out", null, "Browser rendering timed out");

        message.Should().Be(
            "Timed out. Do not retry this URL — try a lighter page or a different tool.");
    }

    [TestMethod]
    public void BuildMessage_WhenServerError_ReturnsRetryOnceGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(500, "HTTP 500", 502, "HTTP 502");

        message.Should().Be(
            "Server error (5xx). You may retry once; if it fails again, use a different source.");
    }

    [TestMethod]
    public void BuildMessage_WhenEmptyContent_ReturnsDifferentToolGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(200, "HTML was empty or exceeded max size", 200, "HTTP 200");

        message.Should().Be(
            "Page returned no usable content. This may be an API endpoint or JS-only page — use a different tool.");
    }
}
