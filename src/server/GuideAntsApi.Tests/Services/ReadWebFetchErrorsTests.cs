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
            "404 Not Found. This URL does not exist. Do not retry or issue another ReadWeb tool call for this invocation.");
    }

    [TestMethod]
    public void BuildMessage_WhenForbiddenDominatesNotFound_ReturnsForbiddenGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(403, "HTTP 403", 404, "HTTP 404");

        message.Should().Be(
            "403 Forbidden. This host blocks unauthenticated access. Do not retry or issue another ReadWeb tool call for this invocation.");
    }

    [TestMethod]
    public void BuildMessage_WhenTimedOut_ReturnsTimeoutGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(null, "Direct fetch timed out", null, "Browser rendering timed out");

        message.Should().Be(
            "Timed out. Do not retry or issue another ReadWeb tool call for this invocation.");
    }

    [TestMethod]
    public void BuildMessage_WhenServerError_ReturnsTerminalGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(500, "HTTP 500", 502, "HTTP 502");

        message.Should().Be(
            "Server error (5xx). Do not retry or issue another ReadWeb tool call for this invocation.");
    }

    [TestMethod]
    public void BuildMessage_WhenEmptyContent_ReturnsDifferentToolGuidance()
    {
        var message = ReadWebFetchErrors.BuildMessage(200, "HTML was empty or exceeded max size", 200, "HTTP 200");

        message.Should().Be(
            "Page returned no usable content. Do not retry or issue another ReadWeb tool call for this invocation.");
    }

    [TestMethod]
    public void BuildMessage_ForEveryFailureKind_DoesNotSuggestRecovery()
    {
        var messages = new[]
        {
            ReadWebFetchErrors.BuildMessage(401, "HTTP 401", null, null),
            ReadWebFetchErrors.BuildMessage(429, "HTTP 429", null, null),
            ReadWebFetchErrors.BuildMessage(null, "Direct fetch failed", null, "Browser render failed")
        };

        messages.Should().AllSatisfy(message =>
        {
            message.Should().Contain("Do not retry or issue another ReadWeb tool call for this invocation.");
            message.Should().NotContain("different source");
            message.Should().NotContain("different tool");
            message.Should().NotContain("retry once");
        });
    }
}
