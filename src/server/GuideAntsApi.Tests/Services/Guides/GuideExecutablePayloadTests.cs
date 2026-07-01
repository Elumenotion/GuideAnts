using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideExecutablePayloadTests
{
    [TestMethod]
    public void HasSkillScriptsPayload_IgnoresCodeInterpreterFiles()
    {
        var files = new[]
        {
            new AssistantFile
            {
                FolderKind = "CodeInterpreter",
                RelativePath = "run.py",
            },
        };

        GuideExecutablePayload.HasSkillScriptsPayload(files).Should().BeFalse();
    }

    [TestMethod]
    public void EnsureRunPythonToolForSkillPayload_AddsCatalogToolWhenSkillScriptsExist()
    {
        var assistantId = Guid.NewGuid();
        var assistant = new Assistant
        {
            Id = assistantId,
            Name = "Guide",
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/scripts/run.py",
                    ContentBytes = Encoding.UTF8.GetBytes("print('ok')"),
                },
            ],
        };

        GuideExecutablePayload.EnsureRunPythonToolForSkillPayload(assistant);

        assistant.Tools.Should().ContainSingle();
        assistant.Tools[0].ToolId.Should().Be(GuideExecutablePayload.RunPythonToolId);
    }

    [TestMethod]
    public void SkillToolsetMapping_TreatsRunPythonAsSandboxCapability()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_python" };

        SkillToolsetMapping.IsToolsetAvailable("sandbox", available).Should().BeTrue();
        SkillToolsetMapping.IsToolsetAvailable("terminal", available).Should().BeTrue();
    }
}
