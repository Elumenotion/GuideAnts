using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class AssistantSkillMetaSyncTests
{
    private const string SkillMarkdown = """
---
name: sidecar-skill
description: Sidecar test skill.
metadata:
  guideants:
    enabled: true
    display_order: 7
---
# Body
""";

    [TestMethod]
    public async Task SyncAssistantAsync_CreatesMetadataRows_WithoutBodies()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"skill-meta-sync-{Guid.NewGuid():N}")
            .Options;

        var assistantId = Guid.NewGuid();
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
                Id = Guid.NewGuid(),
                AssistantId = assistantId,
                FolderKind = "Skill",
                RelativePath = "Skills/sidecar-skill/SKILL.md",
                ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
                Created = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var syncContext = new ApplicationDbContext(options))
        {
            var sync = new AssistantSkillMetaSync(syncContext);
            await sync.SyncAssistantAsync(assistantId);
        }

        await using var verify = new ApplicationDbContext(options);
        var meta = await verify.AssistantSkillMetas.SingleAsync();
        meta.SkillName.Should().Be("sidecar-skill");
        meta.Description.Should().Be("Sidecar test skill.");
        meta.Enabled.Should().BeTrue();
        meta.DisplayOrder.Should().Be(7);
        meta.ContentHash.Should().Be(AssistantSkillMetaSync.ComputeContentHash(
            Encoding.UTF8.GetBytes(SkillMarkdown)));
        (await verify.AssistantFileMarkdownShadows.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public void SkillDtoBuilder_UsesSidecar_WhenHashMatches()
    {
        var bytes = Encoding.UTF8.GetBytes(SkillMarkdown);
        var hash = AssistantSkillMetaSync.ComputeContentHash(bytes);
        var assistantId = Guid.NewGuid();
        var file = new AssistantFile
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = "Skills/sidecar-skill/SKILL.md",
            ContentBytes = bytes,
            Created = DateTime.UtcNow,
        };
        var meta = new AssistantSkillMeta
        {
            AssistantId = assistantId,
            SkillName = "sidecar-skill",
            Description = "From sidecar",
            Enabled = false,
            DisplayOrder = 99,
            ContentHash = hash,
            Created = DateTime.UtcNow,
        };

        var skills = SkillDtoBuilder.BuildFromAssistantFiles([file], [meta]);

        skills.Should().ContainSingle();
        skills[0].Description.Should().Be("From sidecar");
        skills[0].Enabled.Should().BeFalse();
        skills[0].DisplayOrder.Should().Be(99);
    }

    [TestMethod]
    public void SkillDtoBuilder_IgnoresStaleSidecar_WhenHashDiffers()
    {
        var bytes = Encoding.UTF8.GetBytes(SkillMarkdown);
        var assistantId = Guid.NewGuid();
        var file = new AssistantFile
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = "Skills/sidecar-skill/SKILL.md",
            ContentBytes = bytes,
            Created = DateTime.UtcNow,
        };
        var meta = new AssistantSkillMeta
        {
            AssistantId = assistantId,
            SkillName = "sidecar-skill",
            Description = "Stale sidecar",
            Enabled = false,
            DisplayOrder = 99,
            ContentHash = "deadbeef",
            Created = DateTime.UtcNow,
        };

        var skills = SkillDtoBuilder.BuildFromAssistantFiles([file], [meta]);

        skills[0].Description.Should().Be("Sidecar test skill.");
        skills[0].Enabled.Should().BeTrue();
        skills[0].DisplayOrder.Should().Be(7);
    }
}
