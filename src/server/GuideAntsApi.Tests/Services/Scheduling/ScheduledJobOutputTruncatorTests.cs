using FluentAssertions;
using GuideAntsApi.Services.Scheduling;

namespace GuideAntsApi.Tests.Services.Scheduling;

[TestClass]
public sealed class ScheduledJobOutputTruncatorTests
{
    [TestMethod]
    public void TruncateErrorMessage_LeavesShortMessagesUnchanged()
    {
        ScheduledJobOutputTruncator.TruncateErrorMessage("script failed")
            .Should().Be("script failed");
    }

    [TestMethod]
    public void TruncateErrorMessage_TruncatesToDatabaseLimit()
    {
        var longMessage = new string('x', ScheduledJobOutputTruncator.MaxErrorMessageCharacters + 500);

        var truncated = ScheduledJobOutputTruncator.TruncateErrorMessage(longMessage);

        truncated.Should().NotBeNull();
        truncated!.Length.Should().BeLessThanOrEqualTo(ScheduledJobOutputTruncator.MaxErrorMessageCharacters);
        truncated.Should().EndWith("[... error message truncated for length ...]");
    }
}
