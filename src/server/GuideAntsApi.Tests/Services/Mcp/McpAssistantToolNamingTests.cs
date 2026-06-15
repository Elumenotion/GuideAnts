using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpAssistantToolNamingTests
{
    [TestMethod]
    public void AssignToolNames_Sanitizes_and_dedupes()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (Guid.NewGuid(), "Creative Guide", "Guide desc", true),
            (Guid.NewGuid(), "Copy Editor", "Edits copy", false),
            (Guid.NewGuid(), "Copy-Editor", "Duplicate-ish", false)
        ]);

        roster.Should().HaveCount(3);
        roster[0].ToolName.Should().Be("Creative_Guide");
        roster[1].ToolName.Should().Be("Copy_Editor");
        roster[2].ToolName.Should().Be("Copy-Editor");
    }

    [TestMethod]
    public void FindByToolName_is_case_insensitive()
    {
        var id = Guid.NewGuid();
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (id, "Researcher", "Researches", false)
        ]);

        var found = McpPublishedAssistantCatalog.FindByToolName(roster, "researcher");
        found.Should().NotBeNull();
        found!.AssistantId.Should().Be(id);
    }
}
