using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class UnresolvedErrorsExtractorTests
{
    [TestMethod]
    public void Extract_NoErrors_ReturnsEmpty()
    {
        var ledger = new List<ActivityLedgerEntry> { new("ReadFile", "a.txt", true) };

        UnresolvedErrorsExtractor.Extract(ledger).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_ErrorWithNoLaterSuccess_IsUnresolved()
    {
        var ledger = new List<ActivityLedgerEntry> { new("RunScript", "build.sh", false) };

        var result = UnresolvedErrorsExtractor.Extract(ledger);

        result.Should().ContainSingle();
        result[0].ToolName.Should().Be("RunScript");
    }

    [TestMethod]
    public void Extract_ErrorFollowedByLaterSuccessOnSameTool_IsResolved()
    {
        var ledger = new List<ActivityLedgerEntry>
        {
            new("RunScript", "build.sh", false),
            new("RunScript", "build.sh", true)
        };

        UnresolvedErrorsExtractor.Extract(ledger).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_LaterSuccessOnADifferentTool_DoesNotResolveTheError()
    {
        var ledger = new List<ActivityLedgerEntry>
        {
            new("RunScript", "build.sh", false),
            new("ReadFile", "log.txt", true)
        };

        UnresolvedErrorsExtractor.Extract(ledger).Should().ContainSingle();
    }

    [TestMethod]
    public void Extract_UnknownOutcome_IsNotTreatedAsAnError()
    {
        var ledger = new List<ActivityLedgerEntry> { new("SearchProject", "q", null) };

        UnresolvedErrorsExtractor.Extract(ledger).Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_EarlierSuccessDoesNotResolveALaterError()
    {
        var ledger = new List<ActivityLedgerEntry>
        {
            new("RunScript", "build.sh", true),
            new("RunScript", "build.sh", false)
        };

        UnresolvedErrorsExtractor.Extract(ledger).Should().ContainSingle();
    }
}
