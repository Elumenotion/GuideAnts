using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
[DoNotParallelize]
public sealed class SkillToolsTests
{
    private static AssistantDefinition CreateDefinition(Guid assistantId, string skillName = "demo")
    {
        var folder = $"Skills/{skillName}";
        return new AssistantDefinition
        {
            Id = assistantId,
            Name = "Test Assistant",
            Skills =
            [
                new SkillDescriptor
                {
                    Name = skillName,
                    Description = "Demo skill",
                    FolderPath = folder,
                    Locator = $"skill://{assistantId}/{skillName}",
                    Files =
                    [
                        $"{folder}/SKILL.md",
                        $"{folder}/references/ref.md",
                    ]
                }
            ]
        };
    }

    private static string SkillMarkdown() =>
        """
---
name: demo
description: Demo skill
---
# Skill body content
""";

    [TestMethod]
    public async Task ReadSkill_ReturnsBodyWithoutFrontmatter()
    {
        var assistantId = Guid.NewGuid();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"skills-read-{Guid.NewGuid():N}");
        await SeedSkillFileAsync(options, assistantId, "Skills/demo/SKILL.md", SkillMarkdown());

        InitializeSkillTools(options);
        var def = CreateDefinition(assistantId);

        var json = await SkillTools.ReadSkill($"skill://{assistantId}/demo", assistantDefinition: def);
        json.Should().Contain("Skill body content");
        json.Should().NotContain("name: demo");
    }

    [TestMethod]
    public async Task ReadSkill_ReturnsReferenceFile()
    {
        var assistantId = Guid.NewGuid();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"skills-ref-{Guid.NewGuid():N}");
        await SeedSkillFileAsync(options, assistantId, "Skills/demo/SKILL.md", SkillMarkdown());
        await SeedSkillFileAsync(options, assistantId, "Skills/demo/references/ref.md", "reference text");

        InitializeSkillTools(options);
        var def = CreateDefinition(assistantId);

        var json = await SkillTools.ReadSkill(
            $"skill://{assistantId}/demo",
            file_path: "references/ref.md",
            assistantDefinition: def);

        json.Should().Contain("reference text");
    }

    [TestMethod]
    public async Task ReadSkill_RejectsPathTraversal()
    {
        var assistantId = Guid.NewGuid();
        InitializeSkillTools(BackgroundJobTestHelpers.CreateInMemoryOptions($"skills-traversal-{Guid.NewGuid():N}"));
        var def = CreateDefinition(assistantId);

        var json = await SkillTools.ReadSkill(
            $"skill://{assistantId}/demo",
            file_path: "../../etc/passwd",
            assistantDefinition: def);

        json.Should().Contain("error");
        json.Should().Contain("traversal");
    }

    [TestMethod]
    public async Task ReadSkill_RejectsAbsolutePath()
    {
        var assistantId = Guid.NewGuid();
        InitializeSkillTools(BackgroundJobTestHelpers.CreateInMemoryOptions($"skills-abs-{Guid.NewGuid():N}"));
        var def = CreateDefinition(assistantId);

        var json = await SkillTools.ReadSkill(
            $"skill://{assistantId}/demo",
            file_path: "/abs/path",
            assistantDefinition: def);

        json.Should().Contain("error");
        json.Should().Contain("Absolute");
    }

    [TestMethod]
    public async Task ReadSkill_RejectsCrossSkillPath()
    {
        var assistantId = Guid.NewGuid();
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"skills-cross-{Guid.NewGuid():N}");
        await SeedSkillFileAsync(options, assistantId, "Skills/demo/SKILL.md", SkillMarkdown());
        await SeedSkillFileAsync(options, assistantId, "Skills/other/secret.md", "secret");

        InitializeSkillTools(options);
        var def = CreateDefinition(assistantId);
        def.Skills![0].Files.Add("Skills/other/secret.md");

        var json = await SkillTools.ReadSkill(
            $"skill://{assistantId}/demo",
            file_path: "../other/secret.md",
            assistantDefinition: def);

        json.Should().Contain("error");
    }

    [TestMethod]
    public async Task ListSkills_AppliesGatingFilter()
    {
        var assistantId = Guid.NewGuid();
        InitializeSkillTools(BackgroundJobTestHelpers.CreateInMemoryOptions($"skills-list-{Guid.NewGuid():N}"));

        var def = new AssistantDefinition
        {
            Id = assistantId,
            Skills =
            [
                new SkillDescriptor
                {
                    Name = "visible",
                    Description = "Visible",
                    FolderPath = "Skills/visible",
                    Locator = $"skill://{assistantId}/visible",
                    Files = ["Skills/visible/SKILL.md"]
                },
                new SkillDescriptor
                {
                    Name = "hidden",
                    Description = "Hidden",
                    FolderPath = "Skills/hidden",
                    Locator = $"skill://{assistantId}/hidden",
                    RequiresTools = ["MissingTool"],
                    Files = ["Skills/hidden/SKILL.md"]
                }
            ],
            Tools =
            [
                new ToolDefinition
                {
                    Type = "function",
                    Function = new AssistantsApiToolFunctionOneOfType
                    {
                        AsObject = new FunctionDefinition { Name = "WebSearch" }
                    }
                }
            ]
        };

        var json = await SkillTools.ListSkills(assistantDefinition: def);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("name").GetString().Should().Be("visible");
    }

    private static async Task SeedSkillFileAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid assistantId,
        string relativePath,
        string content)
    {
        await using var db = new ApplicationDbContext(options);
        if (!await db.Assistants.AnyAsync(a => a.Id == assistantId))
        {
            db.Assistants.Add(new Assistant
            {
                Id = assistantId,
                Name = "Test",
                Created = DateTime.UtcNow
            });
        }

        db.AssistantFiles.Add(new AssistantFile
        {
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = relativePath,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static void InitializeSkillTools(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(options));
        SkillTools.InitializeServiceProvider(services.BuildServiceProvider());
    }
}
