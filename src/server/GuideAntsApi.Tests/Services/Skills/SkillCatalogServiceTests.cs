using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class SkillCatalogServiceTests
{
    [TestMethod]
    public async Task Publish_CreatesSkillVersionOneWithAllPackageFiles()
    {
        var options = SkillCatalogTestFixtures.NewOptions("publish-v1");
        var (assistantId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);

        Guid skillId;
        await using (var db = new ApplicationDbContext(options))
        {
            var result = await new SkillCatalogService(db)
                .PublishFromAssistantAsync(assistantId, "html-craft");
            result.CreatedNewSkill.Should().BeTrue();
            result.Version.Should().Be(1);
            result.Name.Should().Be("html-craft");
            skillId = result.SkillId;
        }

        await using var verify = new ApplicationDbContext(options);
        var skill = await verify.Skills
            .Include(s => s.Versions).ThenInclude(v => v.Files)
            .SingleAsync(s => s.Id == skillId);
        var current = skill.CurrentSnapshot();
        skill.Description.Should().Be("Build and verify single-file HTML pages.");
        current!.Version.Should().Be(1);
        skill.PublishedFromAssistantId.Should().Be(assistantId);
        current.RequiresToolsetsJson.Should().Contain("bash");
        current.RequiresToolsJson.Should().Contain("run_bash");
        current.Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal("SKILL.md", "references/setup.md", "scripts/build.sh");
        current.Files.Single(f => f.RelativePath == "SKILL.md")
            .ContentBytes!.Should()
            .Equal(Encoding.UTF8.GetBytes(SkillCatalogTestFixtures.SkillMarkdown));
    }

    [TestMethod]
    public async Task Publish_TwiceAdvancesVersionAndKeepsOldSnapshotReadable()
    {
        var options = SkillCatalogTestFixtures.NewOptions("publish-v2");
        var (assistantId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);

        Guid skillId;
        string v1Hash;
        await using (var db = new ApplicationDbContext(options))
        {
            var catalog = new SkillCatalogService(db);
            skillId = (await catalog.PublishFromAssistantAsync(assistantId, "html-craft")).SkillId;
            v1Hash = (await catalog.GetAsync("html-craft", 1))!.ContentHash;

            var second = await catalog.PublishFromAssistantAsync(assistantId, "html-craft");
            second.Version.Should().Be(2);
            second.CreatedNewSkill.Should().BeFalse();
        }

        await using var verify = new ApplicationDbContext(options);
        var versions = await verify.SkillVersions
            .Where(v => v.SkillId == skillId)
            .OrderBy(v => v.Version)
            .ToListAsync();
        versions.Select(v => v.Version).Should().Equal(1, 2);
        versions[0].ContentHash.Should().Be(v1Hash);

        var detail = await new SkillCatalogService(verify).GetAsync("html-craft", 1);
        detail!.CurrentVersion.Should().Be(1);
        detail.Versions.Should().Equal(2, 1);
    }

    [TestMethod]
    public async Task Publish_MissingSkillOrAssistant_Fails()
    {
        var options = SkillCatalogTestFixtures.NewOptions("publish-missing");
        var (assistantId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);
        await using var db = new ApplicationDbContext(options);
        var catalog = new SkillCatalogService(db);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => catalog.PublishFromAssistantAsync(assistantId, "not-here"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => catalog.PublishFromAssistantAsync(Guid.NewGuid(), "html-craft"));
    }

    [TestMethod]
    public async Task Delete_UnpinsInstallsAndRemovesSnapshots()
    {
        var options = SkillCatalogTestFixtures.NewOptions("delete");
        var (sourceId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);
        var assistantId = Guid.NewGuid();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Assistants.Add(new GuideAntsApi.DataModel.Models.Assistant
            {
                Id = assistantId,
                Name = "Target",
                Created = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await new SkillCatalogService(db).PublishFromAssistantAsync(sourceId, "html-craft");
        }

        // Install into the fresh target, then delete the catalog entry.
        await using (var db = new ApplicationDbContext(options))
        {
            await new SkillInstallService(db).InstallAsync(
                assistantId,
                new GuideAntsApi.Models.Guides.SkillInstallRequest("html-craft", null, false));
        }

        int? orphaned;
        await using (var db = new ApplicationDbContext(options))
        {
            orphaned = await new SkillCatalogService(db).DeleteAsync("html-craft");
        }

        orphaned.Should().Be(1);
        await using var verify = new ApplicationDbContext(options);
        verify.Skills.Should().BeEmpty();
        verify.SkillVersions.Should().BeEmpty();
        verify.SkillFiles.Should().BeEmpty();

        // The install survives as a plain local skill: files intact, pin cleared.
        var meta = await verify.AssistantSkillMetas.SingleAsync();
        meta.SkillId.Should().BeNull();
        meta.CatalogVersion.Should().BeNull();
        (await verify.AssistantFiles
            .CountAsync(f => f.FolderKind == "Skill" && f.AssistantId == assistantId))
            .Should().Be(3);
    }

    [TestMethod]
    public async Task Delete_UnknownEntry_ReturnsNull()
    {
        var options = SkillCatalogTestFixtures.NewOptions("delete-unknown");
        await using var db = new ApplicationDbContext(options);
        (await new SkillCatalogService(db).DeleteAsync("ghost")).Should().BeNull();
    }

    [TestMethod]
    public async Task List_FiltersBySearchAndReportsCurrentVersion()
    {
        var options = SkillCatalogTestFixtures.NewOptions("list");
        var (assistantId, _) = await SkillCatalogTestFixtures.SeedAssistantSkillAsync(options);
        await using var db = new ApplicationDbContext(options);
        var catalog = new SkillCatalogService(db);
        await catalog.PublishFromAssistantAsync(assistantId, "html-craft");

        var all = await catalog.ListAsync(null);
        all.Should().ContainSingle();
        all[0].CurrentVersion.Should().Be(1);
        all[0].FileCount.Should().Be(3);

        (await catalog.ListAsync("html")).Should().ContainSingle();
        (await catalog.ListAsync("nomatch")).Should().BeEmpty();
    }
}
