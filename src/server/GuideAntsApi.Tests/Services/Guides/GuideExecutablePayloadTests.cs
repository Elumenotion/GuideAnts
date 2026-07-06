using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
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
    public void HasNotebookPayloadFiles_IncludesCodeInterpreterAndSkillScripts()
    {
        GuideExecutablePayload.HasNotebookPayloadFiles(
        [
            new AssistantFile { FolderKind = "CodeInterpreter", RelativePath = "run.py" },
        ]).Should().BeTrue();

        GuideExecutablePayload.HasNotebookPayloadFiles(
        [
            new AssistantFile
            {
                FolderKind = "Skill",
                RelativePath = "Skills/demo/scripts/run.py",
            },
        ]).Should().BeTrue();

        GuideExecutablePayload.HasNotebookPayloadFiles(
        [
            new AssistantFile
            {
                FolderKind = "Skill",
                RelativePath = "Skills/demo/references/guide.md",
            },
        ]).Should().BeFalse();
    }

    [TestMethod]
    public void EnsureFilesContextOption_AddsWhenNotebookPayloadExists()
    {
        var assistantId = Guid.NewGuid();
        var assistant = new Assistant
        {
            Id = assistantId,
            Name = "crew",
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

        GuideExecutablePayload.EnsureFilesContextOption(assistant);

        assistant.ContextOptions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Key = GuideExecutablePayload.FilesContextOptionKey,
                Value = GuideExecutablePayload.FilesContextOptionValue,
            }, options => options.ExcludingMissingMembers());
    }

    [TestMethod]
    public void EnsureFilesContextOption_SkipsWhenFilesMarkerAlreadyPresent()
    {
        var assistantId = Guid.NewGuid();
        var assistant = new Assistant
        {
            Id = assistantId,
            Name = "crew",
            ContextOptions =
            [
                new AssistantContextOption
                {
                    AssistantId = assistantId,
                    Key = "workspace",
                    Value = "[@files]",
                },
            ],
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/scripts/run.py",
                },
            ],
        };

        GuideExecutablePayload.EnsureFilesContextOption(assistant);

        assistant.ContextOptions.Should().ContainSingle()
            .Which.Key.Should().Be("workspace");
    }

    [TestMethod]
    public void EnsureFilesContextOption_SkipsWhenNoNotebookPayload()
    {
        var assistant = new Assistant
        {
            Id = Guid.NewGuid(),
            Name = "crew",
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "VectorStore",
                    RelativePath = "docs/readme.md",
                },
            ],
        };

        GuideExecutablePayload.EnsureFilesContextOption(assistant);

        assistant.ContextOptions.Should().BeEmpty();
    }

    [TestMethod]
    public void NewUploadsHaveNotebookPayload_DetectsSkillScriptsAndCodeInterpreter()
    {
        GuideExecutablePayload.NewUploadsHaveNotebookPayload(
        [
            new FileUploadDto(
                "Skill",
                null,
                "Skills/demo/scripts/run.py",
                [1],
                "text/plain"),
        ]).Should().BeTrue();

        GuideExecutablePayload.NewUploadsHaveNotebookPayload(
        [
            new FileUploadDto(
                "CodeInterpreter",
                null,
                "placeholder.txt",
                [1],
                "text/plain"),
        ]).Should().BeTrue();

        GuideExecutablePayload.NewUploadsHaveNotebookPayload(
        [
            new FileUploadDto(
                "VectorStore",
                null,
                "readme.md",
                [1],
                "text/markdown"),
        ]).Should().BeFalse();
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

        assistant.Tools.Should().ContainSingle()
            .Which.ToolId.Should().Be(GuideExecutablePayload.RunPythonToolId);
    }

    [TestMethod]
    public void SkillToolsetMapping_TreatsRunPythonAsSandboxCapability()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_python" };

        SkillToolsetMapping.IsToolsetAvailable("sandbox", available).Should().BeTrue();
        SkillToolsetMapping.IsToolsetAvailable("terminal", available).Should().BeTrue();
    }
}
