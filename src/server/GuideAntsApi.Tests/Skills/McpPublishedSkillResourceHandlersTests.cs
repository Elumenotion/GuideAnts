using System.Reflection;
using System.Text;
using AntRunner.Chat;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Mcp;
using GuideAntsApi.Services.Skills;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
[DoNotParallelize]
public sealed class McpPublishedSkillResourceHandlersTests
{
    [TestMethod]
    public async Task ListVisibleSkillsAsync_UsesPublishedLocatorAndGating()
    {
        var assistantId = Guid.NewGuid();
        var guideName = $"Guide-{Guid.NewGuid():N}";
        SeedAssistantCache(guideName, CreateDefinition(assistantId, guideName));

        var entries = await PublishedSkillCatalog.ListVisibleSkillsAsync(
            guideName,
            [new McpAddressableAssistant(assistantId, guideName, "guide", "Guide", true)]);

        entries.Should().ContainSingle();
        entries[0].PublishedLocator.Should().Be(SkillLocator.FormatPublished(guideName, "demo"));
    }

    [TestMethod]
    public async Task ReadSkill_PublishedLocator_ReturnsBody()
    {
        var assistantId = Guid.NewGuid();
        var guideName = $"Guide-{Guid.NewGuid():N}";
        var options = await SeedSkillFilesAsync(assistantId);
        InitializeSkillTools(options);
        var def = CreateDefinition(assistantId, guideName);

        var json = await SkillTools.ReadSkill(
            SkillLocator.FormatPublished(guideName, "demo"),
            assistantDefinition: def);

        json.Should().Contain("Skill body content");
    }

    [TestMethod]
    public async Task ReadSkill_PublishedReferenceLocator_ReturnsReference()
    {
        var assistantId = Guid.NewGuid();
        var guideName = $"Guide-{Guid.NewGuid():N}";
        var options = await SeedSkillFilesAsync(assistantId);
        InitializeSkillTools(options);
        var def = CreateDefinition(assistantId, guideName);

        var json = await SkillTools.ReadSkill(
            SkillLocator.FormatPublishedReference(guideName, "demo", "ref.md"),
            assistantDefinition: def);

        json.Should().Contain("reference text");
    }

    [TestMethod]
    public void SkillTools_AreLocalFunction_NotClientHandled()
    {
        var registry = AntRunner.ToolCalling.ToolContractRegistry.GetAllToolOperations();
        registry.Should().ContainKey("skills.list");
        registry.Should().ContainKey("skills.read");
    }

    [TestMethod]
    public void PublishedWireHandlers_DoNotContainSkillsBranching()
    {
        var wireDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "GuideAntsApi", "Endpoints", "PublishedWire"));

        foreach (var file in Directory.EnumerateFiles(wireDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("skills.list", because: file);
            text.Should().NotContain("skills.read", because: file);
            text.Should().NotContain("SkillTools", because: file);
            text.Should().NotContain("SkillDiscovery", because: file);
        }
    }

    private static AssistantDefinition CreateDefinition(Guid assistantId, string guideName, string skillName = "demo")
    {
        var folder = $"Skills/{skillName}";
        return new AssistantDefinition
        {
            Id = assistantId,
            Name = guideName,
            Skills =
            [
                new SkillDescriptor
                {
                    Name = skillName,
                    Description = "Demo skill",
                    FolderPath = folder,
                    Locator = SkillLocator.FormatInternal(assistantId, skillName),
                    Files =
                    [
                        $"{folder}/SKILL.md",
                        $"{folder}/references/ref.md",
                    ]
                },
                new SkillDescriptor
                {
                    Name = "hidden",
                    Description = "Hidden",
                    FolderPath = "Skills/hidden",
                    Locator = SkillLocator.FormatInternal(assistantId, "hidden"),
                    RequiresTools = ["MissingTool"],
                    Files = ["Skills/hidden/SKILL.md"]
                }
            ]
        };
    }

    private static async Task<DbContextOptions<ApplicationDbContext>> SeedSkillFilesAsync(Guid assistantId)
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"mcp-skill-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        db.Assistants.Add(new Assistant { Id = assistantId, Name = "seed", Created = DateTime.UtcNow });
        db.AssistantFiles.Add(new AssistantFile
        {
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = "Skills/demo/SKILL.md",
            ContentBytes = Encoding.UTF8.GetBytes("""
---
name: demo
description: Demo skill
---
Skill body content
"""),
            Created = DateTime.UtcNow
        });
        db.AssistantFiles.Add(new AssistantFile
        {
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = "Skills/demo/references/ref.md",
            ContentBytes = Encoding.UTF8.GetBytes("reference text"),
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return options;
    }

    private static void InitializeSkillTools(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(options));
        SkillTools.InitializeServiceProvider(services.BuildServiceProvider());
    }

    private static void SeedAssistantCache(string guideName, AssistantDefinition definition)
    {
        AssistantUtility.ClearCache(guideName);
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, definition, null, DateTime.UtcNow)!;
        var cache = typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var indexer = cache.GetType().GetProperty("Item")!;
        indexer.SetValue(cache, entry, [guideName]);
    }
}
