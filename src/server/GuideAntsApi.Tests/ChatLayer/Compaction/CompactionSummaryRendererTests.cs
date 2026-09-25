using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class CompactionSummaryRendererTests
{
    private static readonly GoalSection EmptyGoal = new(null, []);
    private static readonly ArtifactsSection EmptyArtifacts = new([], []);

    [TestMethod]
    public void Render_StartsWithTheHandoffFramingLine()
    {
        var text = CompactionSummaryRenderer.Render(EmptyGoal, EmptyArtifacts, [], [], [], summarizedMessageCount: 0);

        text.Should().StartWith("The following is a condensed handoff briefing");
    }

    [TestMethod]
    public void Render_IncludesAllFiveSectionHeadings()
    {
        var text = CompactionSummaryRenderer.Render(EmptyGoal, EmptyArtifacts, [], [], [], summarizedMessageCount: 0);

        text.Should().Contain("## Goal");
        text.Should().Contain("## Artifacts");
        text.Should().Contain("## Activity ledger");
        text.Should().Contain("## Unresolved errors");
        text.Should().Contain("## Directives");
    }

    [TestMethod]
    public void Render_DegenerateInput_ShowsPlaceholdersInsteadOfBlankSections()
    {
        var text = CompactionSummaryRenderer.Render(EmptyGoal, EmptyArtifacts, [], [], [], summarizedMessageCount: 0);

        text.Should().Contain("(none recorded)");
        text.Should().Contain("(none)");
        text.Should().Contain("(no tool calls)");
    }

    [TestMethod]
    public void Render_IncludesGoalArtifactsLedgerAndDirectiveContent()
    {
        var goal = new GoalSection("Build a CSV export feature", ["Actually, do JSON instead"]);
        var artifacts = new ArtifactsSection(["out/report.csv"], ["notes.md"]);
        var ledger = new List<ActivityLedgerEntry> { new("ReadFile", "a.txt", true) };
        var errors = new List<UnresolvedError> { new("RunScript", "build.sh") };
        var directives = new List<Directive> { new("Always run tests first") };

        var text = CompactionSummaryRenderer.Render(goal, artifacts, ledger, errors, directives, summarizedMessageCount: 12);

        text.Should().Contain("Build a CSV export feature");
        text.Should().Contain("Actually, do JSON instead");
        text.Should().Contain("out/report.csv");
        text.Should().Contain("notes.md");
        text.Should().Contain("ReadFile");
        text.Should().Contain("RunScript");
        text.Should().Contain("Always run tests first");
        text.Should().Contain("12 earlier message");
    }

    [TestMethod]
    public void Render_SameInputTwice_IsByteIdentical()
    {
        var goal = new GoalSection("Goal text", []);

        var first = CompactionSummaryRenderer.Render(goal, EmptyArtifacts, [], [], [], 3);
        var second = CompactionSummaryRenderer.Render(goal, EmptyArtifacts, [], [], [], 3);

        first.Should().Be(second);
    }
}
