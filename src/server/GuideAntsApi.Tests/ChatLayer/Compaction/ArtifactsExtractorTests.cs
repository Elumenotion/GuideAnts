using AntRunner.Chat.Compaction;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer.Compaction;

[TestClass]
public sealed class ArtifactsExtractorTests
{
    [TestMethod]
    public void Extract_NoTurns_ReturnsEmptySections()
    {
        var result = ArtifactsExtractor.Extract([]);

        result.Created.Should().BeEmpty();
        result.Modified.Should().BeEmpty();
    }

    [TestMethod]
    public void Extract_CollectsCreatedAndModifiedAcrossTurns()
    {
        var turns = new[]
        {
            new TurnFacts(1, ["out/report.csv"], []),
            new TurnFacts(2, [], ["out/report.csv", "notes.md"])
        };

        var result = ArtifactsExtractor.Extract(turns);

        // out/report.csv was created, then merely touched again later - it stays a "created" artifact.
        result.Created.Should().ContainSingle().Which.Should().Be("out/report.csv");
        result.Modified.Should().ContainSingle().Which.Should().Be("notes.md");
    }

    [TestMethod]
    public void Extract_DedupesWithinTheSameList()
    {
        var turns = new[] { new TurnFacts(1, ["a.txt", "a.txt"], []) };

        ArtifactsExtractor.Extract(turns).Created.Should().ContainSingle();
    }

    [TestMethod]
    public void Extract_OrdersByTurnIndexRegardlessOfInputOrder()
    {
        var turns = new[]
        {
            new TurnFacts(2, ["second.txt"], []),
            new TurnFacts(1, ["first.txt"], [])
        };

        ArtifactsExtractor.Extract(turns).Created.Should().Equal("first.txt", "second.txt");
    }

    [TestMethod]
    public void Extract_IgnoresBlankPaths()
    {
        var turns = new[] { new TurnFacts(1, ["", "  ", "real.txt"], []) };

        ArtifactsExtractor.Extract(turns).Created.Should().Equal("real.txt");
    }
}
