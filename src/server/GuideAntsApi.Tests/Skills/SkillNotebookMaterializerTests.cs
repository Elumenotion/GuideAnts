using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class SkillNotebookMaterializerTests
{
    [TestMethod]
    public void IsMaterializablePayloadPath_AcceptsScriptsAndAssetsOnly()
    {
        SkillNotebookMaterializer.IsMaterializablePayloadPath("Skills/arxiv/scripts/search_arxiv.py").Should().BeTrue();
        SkillNotebookMaterializer.IsMaterializablePayloadPath("Skills/arxiv/assets/template.md.tmpl").Should().BeTrue();
        SkillNotebookMaterializer.IsMaterializablePayloadPath("Skills/arxiv/SKILL.md").Should().BeFalse();
        SkillNotebookMaterializer.IsMaterializablePayloadPath("Skills/arxiv/references/guide.md").Should().BeFalse();
    }

    [TestMethod]
    public void ToNotebookResourcePath_PreservesSkillTreeForGuideAndCrew()
    {
        SkillNotebookMaterializer.ToNotebookResourcePath("Skills/arxiv/scripts/search_arxiv.py", crewSafeName: null)
            .Should().Be("Resources/Skills/arxiv/scripts/search_arxiv.py");

        SkillNotebookMaterializer.ToNotebookResourcePath("Skills/arxiv/scripts/search_arxiv.py", crewSafeName: "Slide-Shows")
            .Should().Be("Resources/crew-Slide-Shows/Skills/arxiv/scripts/search_arxiv.py");
    }

    [TestMethod]
    public void ToOutputProjectionPath_MirrorsResourcesAfterPrefix()
    {
        SkillNotebookMaterializer.ToOutputProjectionPath("Resources/Skills/arxiv/scripts/search_arxiv.py")
            .Should().Be("Output/Skills/arxiv/scripts/search_arxiv.py");
    }

    [TestMethod]
    public void SymlinkTargetFromOutputFile_ComputesRelativeDepth()
    {
        SkillNotebookMaterializer.SymlinkTargetFromOutputFile(
                "Output/Skills/arxiv/scripts/search_arxiv.py",
                "Resources/Skills/arxiv/scripts/search_arxiv.py")
            .Should().Be("../../../../Resources/Skills/arxiv/scripts/search_arxiv.py");
    }
}
