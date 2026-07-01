using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class AssistantSkillMetaSyncFromSaveTests
{
    private static string FixturePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "client", "playwright", "fixtures", "skills",
            relativePath));

    [TestMethod]
    public async Task SyncFromSkillSavesAsync_PersistsEnabledAndOrder_WithoutMutatingManifest()
    {
        var bytes = await File.ReadAllBytesAsync(FixturePath("kanban-video-orchestrator/SKILL.md"));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"skill-save-sync-{Guid.NewGuid():N}")
            .Options;

        var assistantId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = assistantId,
                Name = "Assistant",
                Created = DateTime.UtcNow,
            });
            seed.AssistantFiles.Add(new AssistantFile
            {
                Id = manifestId,
                AssistantId = assistantId,
                FolderKind = "Skill",
                RelativePath = "Skills/kanban-video-orchestrator/SKILL.md",
                ContentBytes = bytes,
                Created = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var saves = new List<AssistantSkillSaveDto>
        {
            new(
                Name: "kanban-video-orchestrator",
                Description: "ignored on save",
                Enabled: false,
                DisplayOrder: 3,
                Source: "Imported",
                FileIdsToKeep: [manifestId],
                FilesToAdd: null),
        };

        await using (var syncContext = new ApplicationDbContext(options))
        {
            var sync = new AssistantSkillMetaSync(syncContext);
            await sync.SyncFromSkillSavesAsync(assistantId, saves);
        }

        await using var verify = new ApplicationDbContext(options);
        var manifest = await verify.AssistantFiles.SingleAsync();
        manifest.ContentBytes.Should().Equal(bytes);

        var meta = await verify.AssistantSkillMetas.SingleAsync();
        meta.SkillName.Should().Be("kanban-video-orchestrator");
        meta.Enabled.Should().BeFalse();
        meta.DisplayOrder.Should().Be(3);
        meta.ContentHash.Should().Be(AssistantSkillMetaSync.ComputeContentHash(bytes));

        var frontmatter = SkillFrontmatter.Parse(Encoding.UTF8.GetString(bytes));
        frontmatter.Enabled.Should().BeTrue();
        frontmatter.DisplayOrder.Should().Be(0);
    }
}
