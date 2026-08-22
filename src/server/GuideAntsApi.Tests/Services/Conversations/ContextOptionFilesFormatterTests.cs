using FluentAssertions;
using GuideAntsApi.Services.Components.Sync;
using GuideAntsApi.Services.Conversations;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ContextOptionFilesFormatterTests
{
    [TestMethod]
    public void FormatConsole_SmallList_Unchanged()
    {
        var paths = new[] { "readme.md", "report.csv" };

        var formatted = ContextOptionFilesFormatter.FormatConsole(paths);

        formatted.Should().StartWith("```console");
        formatted.Should().EndWith("```");
        formatted.Should().Contain("readme.md");
        formatted.Should().Contain("report.csv");
        formatted.Should().NotContain("omitted");
    }

    [TestMethod]
    public void FormatConsole_ExceedsPathLimit_EmitsTruncationNotice()
    {
        var paths = Enumerable.Range(0, ContextOptionFilesFormatter.MaxListedPaths + 25)
            .Select(i => $"file-{i:D4}.txt")
            .ToList();

        var formatted = ContextOptionFilesFormatter.FormatConsole(paths);

        formatted.Should().StartWith("```console");
        formatted.Should().EndWith("```");
        formatted.Should().Contain("listed 500 of 525");
        formatted.Should().Contain("25 additional path(s) omitted");
        formatted.Should().Contain("truncated to protect context window");
        formatted.Split("file-0499.txt").Length.Should().Be(2);
        formatted.Should().NotContain("file-0500.txt");
    }

    [TestMethod]
    public void FormatConsole_ExceedsCharacterLimit_EmitsTruncationNotice()
    {
        var paths = Enumerable.Range(0, 200)
            .Select(i => new string('a', 200) + $"/file-{i}.txt")
            .ToList();

        var formatted = ContextOptionFilesFormatter.FormatConsole(paths);

        formatted.Length.Should().BeLessThanOrEqualTo(ContextOptionFilesFormatter.MaxOutputCharacters + 4);
        formatted.Should().Contain("truncated to protect context window");
        formatted.Should().Contain("additional path(s) omitted");
    }
}

[TestClass]
public sealed class NotebookArtifactPathExclusionsTests
{
    [TestMethod]
    public void IsExcludedRelativePath_MatchesArtifactDirectories()
    {
        NotebookArtifactPathExclusions.IsExcludedRelativePath("Output/.npm/_cacache/foo").Should().BeTrue();
        NotebookArtifactPathExclusions.IsExcludedRelativePath("Output/node_modules/pkg/index.js").Should().BeTrue();
        NotebookArtifactPathExclusions.IsExcludedRelativePath("Output/.audiocpp-extended/engine-18099.log").Should().BeTrue();
        NotebookArtifactPathExclusions.IsExcludedRelativePath("Output/.wire-attachments/x.bin").Should().BeTrue();
        NotebookArtifactPathExclusions.IsExcludedRelativePath("docs/readme.md").Should().BeFalse();
    }
}
