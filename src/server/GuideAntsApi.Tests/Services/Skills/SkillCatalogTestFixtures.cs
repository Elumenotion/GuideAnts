using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

/// <summary>Shared seeding helpers for the skill catalog / install tests.</summary>
internal static class SkillCatalogTestFixtures
{
    public const string SkillMarkdown = """
---
name: html-craft
description: Build and verify single-file HTML pages.
metadata:
  guideants:
    enabled: true
    display_order: 3
    requires_toolsets:
      - bash
    requires_tools:
      - run_bash
    platforms: []
---
# Body
""";

    public static DbContextOptions<ApplicationDbContext> NewOptions(string tag) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"skill-catalog-{tag}-{Guid.NewGuid():N}")
            .Options;

    public static async Task<(Guid AssistantId, Guid ManifestFileId)> SeedAssistantSkillAsync(
        DbContextOptions<ApplicationDbContext> options,
        string manifestMarkdown = SkillMarkdown,
        string folderName = "html-craft")
    {
        var assistantId = Guid.NewGuid();
        await using var db = new ApplicationDbContext(options);
        db.Assistants.Add(new Assistant
        {
            Id = assistantId,
            Name = "Builder",
            Created = DateTime.UtcNow,
        });
        var manifestId = AddSkillFile(db, assistantId, $"Skills/{folderName}/SKILL.md", manifestMarkdown);
        AddSkillFile(db, assistantId, $"Skills/{folderName}/references/setup.md", "# Setup");
        AddSkillFile(db, assistantId, $"Skills/{folderName}/scripts/build.sh", "echo build");
        await db.SaveChangesAsync();
        return (assistantId, manifestId);
    }

    public static Guid AddSkillFile(
        ApplicationDbContext db,
        Guid assistantId,
        string relativePath,
        string content)
    {
        var id = Guid.NewGuid();
        db.AssistantFiles.Add(new AssistantFile
        {
            Id = id,
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = relativePath,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            ContentType = "text/markdown",
            Created = DateTime.UtcNow,
        });
        return id;
    }
}
