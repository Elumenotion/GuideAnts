using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class SkillInstallServiceTests
{
    /// <summary>Publishes the fixture skill from a throwaway assistant and returns a fresh
    /// target assistant that has no skills of its own.</summary>
    private static async Task<(DbContextOptions<ApplicationDbContext> Options, Guid SourceId, Guid TargetId)>
        SeedPublishedAndTargetAsync(string tag)
    {
        var options = SkillCatalogTestFixtures.NewOptions(tag);
        var (sourceId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);
        var targetId = Guid.NewGuid();
        await using var db = new ApplicationDbContext(options);
        db.Assistants.Add(new Assistant { Id = targetId, Name = "Target", Created = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await new SkillCatalogService(db).PublishFromAssistantAsync(sourceId, "html-craft");
        return (options, sourceId, targetId);
    }

    [TestMethod]
    public async Task Install_CopiesEveryFileAndPinsTheMetaRow()
    {
        var (options, _, targetId) = await SeedPublishedAndTargetAsync("install-happy");
        await using (var db = new ApplicationDbContext(options))
        {
            var dto = await new SkillInstallService(db)
                .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, false));
            dto.Name.Should().Be("html-craft");
            dto.CatalogVersion.Should().Be(1);
            dto.CatalogSkillId.Should().NotBeNull();
        }

        await using var verify = new ApplicationDbContext(options);
        var files = await verify.AssistantFiles
            .Where(f => f.AssistantId == targetId && f.FolderKind == "Skill")
            .ToListAsync();
        files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            "Skills/html-craft/SKILL.md",
            "Skills/html-craft/references/setup.md",
            "Skills/html-craft/scripts/build.sh");

        var manifest = files.Single(f => f.RelativePath.EndsWith("SKILL.md"));
        manifest.ContentBytes!.Should().Equal(Encoding.UTF8.GetBytes(SkillCatalogTestFixtures.SkillMarkdown));
        Encoding.UTF8.GetString(manifest.ContentBytes!)
            .Should().NotContain("Skills/html-craft/", "catalog paths must be stripped on install");

        var meta = await verify.AssistantSkillMetas.SingleAsync(m => m.AssistantId == targetId);
        meta.SkillName.Should().Be("html-craft");
        meta.SkillId.Should().NotBeNull();
        meta.CatalogVersion.Should().Be(1);
        meta.ContentHash.Should().Be(
            AntRunner.ToolCalling.AssistantDefinitions.SkillContentHash.Compute(
                Encoding.UTF8.GetBytes(SkillCatalogTestFixtures.SkillMarkdown)));
    }

    [TestMethod]
    public async Task Install_OverExistingLocalSkill_ThrowsConflictUnlessOverwrite()
    {
        var (options, _, targetId) = await SeedPublishedAndTargetAsync("install-conflict");

        // A local skill of the same name that is NOT catalog-installed.
        await using (var db = new ApplicationDbContext(options))
        {
            SkillCatalogTestFixtures.AddSkillFile(
                db, targetId, "Skills/html-craft/SKILL.md", SkillCatalogTestFixtures.SkillMarkdown);
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            await Assert.ThrowsExactlyAsync<SkillInstallService.SkillConflictException>(
                () => new SkillInstallService(db)
                    .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, false)));
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var dto = await new SkillInstallService(db)
                .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, true));
            dto.CatalogVersion.Should().Be(1);
        }

        await using var verify = new ApplicationDbContext(options);
        (await verify.AssistantSkillMetas.CountAsync(m => m.AssistantId == targetId)).Should().Be(1);
        (await verify.AssistantFiles
            .CountAsync(f => f.AssistantId == targetId && f.FolderKind == "Skill"))
            .Should().Be(3, "overwrite replaces the local package instead of duplicating it");
    }

    [TestMethod]
    public async Task Install_ReinstallOfSameEntry_OverwritesWithoutConflict()
    {
        var (options, _, targetId) = await SeedPublishedAndTargetAsync("install-idempotent");
        await using (var db = new ApplicationDbContext(options))
        {
            await new SkillInstallService(db)
                .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, false));
        }

        // Second install of a pinned skill needs no overwrite flag: it is the same source.
        await using (var db = new ApplicationDbContext(options))
        {
            await new SkillInstallService(db)
                .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, false));
        }

        await using var verify = new ApplicationDbContext(options);
        (await verify.AssistantFiles
            .CountAsync(f => f.AssistantId == targetId && f.FolderKind == "Skill"))
            .Should().Be(3);
    }

    [TestMethod]
    public async Task InstalledCopyIsUnaffectedUntilUpdateFromCatalog()
    {
        var (options, sourceId, targetId) = await SeedPublishedAndTargetAsync("install-pin");
        await using (var db = new ApplicationDbContext(options))
        {
            await new SkillInstallService(db)
                .InstallAsync(targetId, new SkillInstallRequest("html-craft", null, false));
        }

        byte[] installedBefore;
        await using (var db = new ApplicationDbContext(options))
        {
            installedBefore = (await db.AssistantFiles.SingleAsync(f =>
                f.AssistantId == targetId && f.RelativePath == "Skills/html-craft/SKILL.md"))
                .ContentBytes!;
        }

        // Source author edits the skill and republishes v2.
        const string v2 = """
---
name: html-craft
description: Build and verify single-file HTML pages. Now with capture.
metadata:
  guideants:
    enabled: true
    display_order: 3
---
# Body v2
""";
        await using (var db = new ApplicationDbContext(options))
        {
            var manifest = await db.AssistantFiles.SingleAsync(f =>
                f.AssistantId == sourceId && f.RelativePath == "Skills/html-craft/SKILL.md");
            manifest.ContentBytes = Encoding.UTF8.GetBytes(v2);
            await db.SaveChangesAsync();
            (await new SkillCatalogService(db).PublishFromAssistantAsync(sourceId, "html-craft"))
                .Version.Should().Be(2);
        }

        // The installed copy is byte-identical: snapshots are immutable.
        await using (var db = new ApplicationDbContext(options))
        {
            var still = (await db.AssistantFiles.SingleAsync(f =>
                f.AssistantId == targetId && f.RelativePath == "Skills/html-craft/SKILL.md"))
                .ContentBytes!;
            still.Should().Equal(installedBefore);
            (await db.AssistantSkillMetas.SingleAsync(m => m.AssistantId == targetId))
                .CatalogVersion.Should().Be(1);
        }

        // Explicit update advances the install and the pin.
        await using (var db = new ApplicationDbContext(options))
        {
            var dto = await new SkillInstallService(db).UpdateFromCatalogAsync(
                targetId, "html-craft", new SkillUpdateFromStoreRequest(null));
            dto.CatalogVersion.Should().Be(2);
        }

        await using var verify = new ApplicationDbContext(options);
        var updated = (await verify.AssistantFiles.SingleAsync(f =>
            f.AssistantId == targetId && f.RelativePath == "Skills/html-craft/SKILL.md"))
            .ContentBytes!;
        Encoding.UTF8.GetString(updated).Should().Contain("Now with capture.");
        (await verify.AssistantSkillMetas.SingleAsync(m => m.AssistantId == targetId))
            .CatalogVersion.Should().Be(2);
    }

    [TestMethod]
    public async Task UpdateFromCatalog_OnLocalOnlySkill_Fails()
    {
        var options = SkillCatalogTestFixtures.NewOptions("update-local");
        var (assistantId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);
        await using (var db = new ApplicationDbContext(options))
        {
            await new GuideAntsApi.Services.Guides.Skills.AssistantSkillMetaSync(db)
                .SyncAssistantAsync(assistantId);
        }

        await using var verify = new ApplicationDbContext(options);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new SkillInstallService(verify).UpdateFromCatalogAsync(
                assistantId, "html-craft", new SkillUpdateFromStoreRequest(null)));
    }
}
