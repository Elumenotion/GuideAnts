using FluentAssertions;
using AntRunner.Chat;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class AssistantToolWrappersTests
{
    [TestMethod]
    public async Task ReadWeb_WhenInstructionsDoNotContainUrl_ReturnsHelpfulValidationError()
    {
        var result = await AssistantToolWrappers.ReadWeb("Find the best TV shows in 2025 and summarize them.");

        result.StandardOutput.Should().Contain("ERROR: Missing URL for ReadWeb");
        result.StandardOutput.Should().Contain("try again");
        result.StandardOutput.Should().Contain("https://example.com/article");
    }
}
