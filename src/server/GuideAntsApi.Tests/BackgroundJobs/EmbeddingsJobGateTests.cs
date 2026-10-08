using FluentAssertions;

using GuideAntsApi.BackgroundJobs;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class EmbeddingsJobGateTests
{
    [TestMethod]
    public void ShouldDefer_WhenEnabledAndLocalAndNotLoadedAndGatedType()
    {
        var options = new EmbeddingsJobGateOptions
        {
            Enabled = true,
            GatedJobTypes = new HashSet<string>(StringComparer.Ordinal) { "IndexNotebookMarkdownShadow" },
        };

        EmbeddingsJobGate.ShouldDeferJobType("IndexNotebookMarkdownShadow", options, usesLocalEmbeddings: true, loaded: false)
            .Should().BeTrue();
    }

    [TestMethod]
    public void ShouldNotDefer_WhenDisabled()
    {
        var options = new EmbeddingsJobGateOptions
        {
            Enabled = false,
            GatedJobTypes = new HashSet<string>(StringComparer.Ordinal) { "IndexNotebookMarkdownShadow" },
        };

        EmbeddingsJobGate.ShouldDeferJobType("IndexNotebookMarkdownShadow", options, usesLocalEmbeddings: true, loaded: false)
            .Should().BeFalse();
    }

    [TestMethod]
    public void ShouldNotDefer_WhenNotLocal()
    {
        var options = new EmbeddingsJobGateOptions { Enabled = true };

        EmbeddingsJobGate.ShouldDeferJobType("IndexNotebookMarkdownShadow", options, usesLocalEmbeddings: false, loaded: false)
            .Should().BeFalse();
    }

    [TestMethod]
    public void ShouldNotDefer_WhenLoaded()
    {
        var options = new EmbeddingsJobGateOptions { Enabled = true };

        EmbeddingsJobGate.ShouldDeferJobType("IndexNotebookMarkdownShadow", options, usesLocalEmbeddings: true, loaded: true)
            .Should().BeFalse();
    }

    [TestMethod]
    public void ShouldNotDefer_WhenJobTypeNotGated()
    {
        var options = new EmbeddingsJobGateOptions
        {
            Enabled = true,
            GatedJobTypes = new HashSet<string>(StringComparer.Ordinal) { "IndexNotebookMarkdownShadow" },
        };

        EmbeddingsJobGate.ShouldDeferJobType("Test", options, usesLocalEmbeddings: true, loaded: false)
            .Should().BeFalse();
    }

    [TestMethod]
    public void DefaultOptions_IncludeExpectedGatedJobTypes()
    {
        var options = new EmbeddingsJobGateOptions();

        options.GatedJobTypes.Should().Contain("IndexNotebookMarkdownShadow");
        options.GatedJobTypes.Should().Contain("IndexContentMarkdownShadow");
        options.GatedJobTypes.Should().Contain("IndexAssistantFileMarkdownShadow");
        options.GatedJobTypes.Should().Contain("IndexDirectTextFile");
        options.GatedJobTypes.Should().Contain("RebuildEmbeddings");
    }
}
