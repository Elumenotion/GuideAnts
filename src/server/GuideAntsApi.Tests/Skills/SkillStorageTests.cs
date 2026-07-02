using System.Reflection;
using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class SkillStorageTests
{
    private static readonly Type StorageType = typeof(DatabaseStorage);

    private static T Invoke<T>(string method, params object?[] args)
    {
        var mi = StorageType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new InvalidOperationException($"Method {method} not found.");
        return (T)mi.Invoke(null, args)!;
    }

    private static string SkillMarkdown(string name, string description, bool enabled = true, int displayOrder = 0) =>
        $"""
---
name: {name}
description: {description}
metadata:
  guideants:
    enabled: {(enabled ? "true" : "false")}
    display_order: {displayOrder}
---
Body for {name}
""";

    [TestMethod]
    public void BuildSkills_GroupsOrdersAndSkipsDisabled()
    {
        var assistantId = Guid.NewGuid();
        var assistant = new Assistant
        {
            Id = assistantId,
            Name = "Skills Guide",
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/alpha/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("alpha", "Alpha skill", displayOrder: 20))
                },
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/alpha/references/a.md",
                    ContentBytes = Encoding.UTF8.GetBytes("ref")
                },
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/beta/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("beta", "Beta skill", enabled: false))
                },
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/gamma/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("gamma", "Gamma skill", displayOrder: 5))
                },
            ]
        };

        var skills = Invoke<List<SkillDescriptor>>("BuildSkills", assistant);

        skills.Should().HaveCount(2);
        skills[0].Name.Should().Be("gamma");
        skills[1].Name.Should().Be("alpha");
        skills[1].Files.Should().Contain("Skills/alpha/references/a.md");
        skills[1].Locator.Should().Be($"skill://{assistantId}/alpha");
        skills[1].Description.Should().Be("Alpha skill");
        JsonSerializer.Serialize(skills[0]).Should().NotContain("body");
    }

    [TestMethod]
    public void MaterializeAssistant_IncludesSkillsWithoutBodiesInManifest()
    {
        var assistantId = Guid.NewGuid();
        var assistant = new Assistant
        {
            Id = assistantId,
            Name = "Manifest Guide",
            Kind = AssistantKind.Assistant,
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("demo", "Demo skill"))
                }
            ]
        };

        var metadata = Invoke<AssistantStorageMetadata>("MaterializeAssistant", assistant);
        using var manifest = JsonDocument.Parse(metadata.ManifestJson);
        var skills = manifest.RootElement.GetProperty("skills");
        skills.GetArrayLength().Should().Be(1);
        skills[0].GetProperty("name").GetString().Should().Be("demo");
        skills[0].TryGetProperty("body", out _).Should().BeFalse();
        skills[0].TryGetProperty("content", out _).Should().BeFalse();
    }

    [TestMethod]
    public void BuildToolsArray_SkillManifestOnly_DoesNotAddCodeInterpreter()
    {
        var assistant = new Assistant
        {
            Name = "Skill Only",
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("demo", "Demo"))
                }
            ]
        };

        var tools = Invoke<List<object>>("BuildToolsArray", assistant);
        var json = JsonSerializer.Serialize(tools);
        json.Should().NotContain("file_search");
        json.Should().NotContain("code_interpreter");
    }

    [TestMethod]
    public void BuildToolsArray_SkillScripts_AddsCodeInterpreter()
    {
        var assistant = new Assistant
        {
            Name = "Skill Scripts",
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("demo", "Demo"))
                },
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/scripts/run.py",
                    ContentBytes = Encoding.UTF8.GetBytes("print('ok')")
                }
            ]
        };

        var tools = Invoke<List<object>>("BuildToolsArray", assistant);
        JsonSerializer.Serialize(tools).Should().Contain("code_interpreter");
    }

    [TestMethod]
    public void BuildToolResources_SkillFiles_ReturnsNull()
    {
        var assistant = new Assistant
        {
            Name = "Skill Only",
            Files =
            [
                new AssistantFile
                {
                    FolderKind = "Skill",
                    RelativePath = "Skills/demo/SKILL.md",
                    ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown("demo", "Demo"))
                }
            ]
        };

        Invoke<object?>("BuildToolResources", assistant).Should().BeNull();
        Invoke<Dictionary<string, byte[]>?>("BuildVectorStoreFiles", assistant).Should().BeNull();
    }
}
