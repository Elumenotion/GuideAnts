using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides.Skills;
using GuideAntsApi.Services.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

/// <summary>
/// The fork rule: a catalog-pinned install keeps its pin only while the local SKILL.md still
/// matches the pinned snapshot. Editing it detaches the pin so it becomes a plain local skill.
/// </summary>
[TestClass]
public sealed class AssistantSkillMetaSyncForkTests
{
    private static async Task<(DbContextOptions<ApplicationDbContext> Options, Guid TargetId)>
        SeedPinnedInstallAsync(string tag)
    {
        var options = SkillCatalogTestFixtures.NewOptions(tag);
        var (sourceId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);
        var targetId = Guid.NewGuid();
        await using var db = new ApplicationDbContext(options);
        db.Assistants.Add(new Assistant { Id = targetId, Name = "Target", Created = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await new SkillCatalogService(db).PublishFromAssistantAsync(sourceId, "html-craft");
        await new SkillInstallService(db)
            .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, false));
        return (options, targetId);
    }

    [TestMethod]
    public async Task Sync_UnchangedInstalledSkill_KeepsPin()
    {
        var (options, targetId) = await SeedPinnedInstallAsync("fork-keep");
        await using var db = new ApplicationDbContext(options);
        await new AssistantSkillMetaSync(db).SyncAssistantAsync(targetId);

        var meta = await db.AssistantSkillMetas.SingleAsync(m => m.AssistantId == targetId);
        meta.SkillId.Should().NotBeNull("unchanged bytes must not fork");
        meta.CatalogVersion.Should().Be(1);
    }

    [TestMethod]
    public async Task Sync_EditedInstalledSkill_ClearsPin()
    {
        var (options, targetId) = await SeedPinnedInstallAsync("fork-edit");
        const string edited = """
---
name: html-craft
description: Build and verify single-file HTML pages.
metadata:
  guideants:
    enabled: true
    display_order: 3
---
# Body edited locally
""";

        await using (var db = new ApplicationDbContext(options))
        {
            var manifest = await db.AssistantFiles.SingleAsync(f =>
                f.AssistantId == targetId && f.RelativePath == "Skills/html-craft/SKILL.md");
            manifest.ContentBytes = Encoding.UTF8.GetBytes(edited);
            await db.SaveChangesAsync();
            await new AssistantSkillMetaSync(db).SyncAssistantAsync(targetId);
        }

        await using var verify = new ApplicationDbContext(options);
        var meta = await verify.AssistantSkillMetas.SingleAsync(m => m.AssistantId == targetId);
        meta.SkillId.Should().BeNull("local edit must fork away from the catalog");
        meta.CatalogVersion.Should().BeNull();
    }

    [TestMethod]
    public async Task Sync_ForkedSkill_ThenRepublishFromSource_DoesNotReattach()
    {
        var (options, targetId) = await SeedPinnedInstallAsync("fork-sticky");
        await using (var db = new ApplicationDbContext(options))
        {
            var manifest = await db.AssistantFiles.SingleAsync(f =>
                f.AssistantId == targetId && f.RelativePath == "Skills/html-craft/SKILL.md");
            manifest.ContentBytes = Encoding.UTF8.GetBytes(
                SkillCatalogTestFixtures.SkillMarkdown + "\nTrailing local edit.\n");
            await db.SaveChangesAsync();
            await new AssistantSkillMetaSync(db).SyncAssistantAsync(targetId);
        }

        // Re-running the sync must not silently re-pin a forked skill.
        await using var verify = new ApplicationDbContext(options);
        await new AssistantSkillMetaSync(verify).SyncAssistantAsync(targetId);
        (await verify.AssistantSkillMetas.SingleAsync(m => m.AssistantId == targetId))
            .SkillId.Should().BeNull();
    }
}
