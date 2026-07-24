using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.Guides.Skills;
using GuideAntsApi.Tests.Services.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class GuidesServiceSkillsRoundTripTests
{
  private const string SkillMarkdown = """
---
name: roundtrip-skill
description: Round-trip test skill.
metadata:
  guideants:
    enabled: true
    display_order: 3
    source: authored
---
# Skill body
""";

    [TestMethod]
    public void SkillDtoBuilder_BuildFromAssistantFiles_GroupsSkillRows()
    {
        var assistantId = Guid.NewGuid();
        var skillFile = new AssistantFile
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            FolderKind = "Skill",
            RelativePath = "Skills/roundtrip-skill/SKILL.md",
            ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
            Created = DateTime.UtcNow,
        };

        var skills = SkillDtoBuilder.BuildFromAssistantFiles([skillFile]);

        skills.Should().ContainSingle();
        skills[0].Name.Should().Be("roundtrip-skill");
        skills[0].Source.Should().Be("Authored");
        skills[0].Files.Should().ContainSingle().Which.RelativePath.Should()
            .Be("Skills/roundtrip-skill/SKILL.md");
    }

    [TestMethod]
    public async Task SkillFile_DoesNotCreateMarkdownShadow_OnDirectInsert()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"skill-roundtrip-{Guid.NewGuid():N}")
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
                RelativePath = "Skills/roundtrip-skill/SKILL.md",
                ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
                Created = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var verify = new ApplicationDbContext(options);
        (await verify.AssistantFileMarkdownShadows.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public void FlattenSkillUploads_RejectsNonSkillFolderKind()
    {
        var skills = new List<AssistantSkillSaveDto>
        {
            new(
                "demo",
                "Demo",
                true,
                0,
                "Imported",
                null,
                [
                    new FileUploadDto(
                        "VectorStore",
                        null,
                        "Skills/demo/SKILL.md",
                        Encoding.UTF8.GetBytes("x"),
                        "text/markdown"),
                ]),
        };

        var act = () => SkillDtoBuilder.FlattenSkillUploads(skills);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FolderKind 'Skill'*");
    }

    [TestMethod]
    public void ShouldPruneGuideSkillFiles_metadata_only_save_does_not_prune()
    {
        var skills = new List<AssistantSkillSaveDto>
        {
            new("alpha", "Alpha", true, 0, "Imported", [], null),
            new("beta", "Beta", true, 1, "Imported", [], null),
        };

        SkillDtoBuilder.ShouldPruneGuideSkillFiles(skills).Should().BeFalse();
        SkillDtoBuilder.ShouldPruneGuideSkillFiles(null).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPruneGuideSkillFiles_prunes_when_file_ids_or_uploads_present()
    {
        var keepId = Guid.NewGuid();
        SkillDtoBuilder.ShouldPruneGuideSkillFiles([
            new("alpha", "Alpha", true, 0, "Imported", [keepId], null),
        ]).Should().BeTrue();

        SkillDtoBuilder.ShouldPruneGuideSkillFiles([
            new(
                "alpha",
                "Alpha",
                true,
                0,
                "Imported",
                null,
                [new FileUploadDto("Skill", null, "Skills/alpha/SKILL.md", [1], "text/markdown")]),
        ]).Should().BeTrue();

        SkillDtoBuilder.ShouldPruneGuideSkillFiles([]).Should().BeTrue();
    }

    [TestMethod]
    public async Task UpdateGuideAsync_metadata_only_skill_save_does_not_delete_skill_files()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"guide-skill-prune-{Guid.NewGuid():N}")
            .Options;

        var guideId = Guid.NewGuid();
        var skillFileId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "SOX Guide",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Created = DateTime.UtcNow,
            });
            seed.AssistantFiles.Add(new AssistantFile
            {
                Id = skillFileId,
                AssistantId = guideId,
                FolderKind = "Skill",
                RelativePath = "Skills/roundtrip-skill/SKILL.md",
                ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
                Created = DateTime.UtcNow,
            });
            seed.AssistantSkillMetas.Add(new AssistantSkillMeta
            {
                AssistantId = guideId,
                SkillName = "roundtrip-skill",
                Description = "Round-trip test skill.",
                Enabled = true,
                DisplayOrder = 0,
                ContentHash = "hash",
                Created = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);
        await service.UpdateGuideAsync(
            guideId,
            new UpdateGuideDto(
                Name: "SOX Guide",
                Description: "desc",
                Instructions: "",
                HomePageMarkdown: "",
                ModelId: null,
                Temperature: null,
                TopP: null,
                ReasoningEffort: null,
                SamplingParametersJson: null,
                AvatarImageBytes: null,
                AvatarContentType: null,
                ToolIds: null,
                CustomTools: null,
                ContextOptions: null,
                AuthProviders: null,
                FileIdsToKeep: [],
                FilesToAdd: null,
                ConversationStarters: null,
                CrewMemberIds: null)
            {
                Skills =
                [
                    new AssistantSkillSaveDto(
                        "roundtrip-skill",
                        "Round-trip test skill.",
                        true,
                        0,
                        "Imported",
                        [],
                        null),
                ],
            });

        await using var verify = new ApplicationDbContext(options);
        (await verify.AssistantFiles.CountAsync(f => f.AssistantId == guideId && f.FolderKind == "Skill"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task CreateAssistantAsync_FromSkillPayload_PersistsScriptsWithoutSkillPackage()
    {
        const string skillMarkdown = """
---
name: searxng-search
description: SearXNG meta-search.
---
# SearXNG Search
""";
        const string scriptContent = "#!/bin/bash\necho searxng";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"create-from-skill-{Guid.NewGuid():N}")
            .Options;

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = new CreateAssistantDto(
            Name: "searxng-search",
            Description: "SearXNG meta-search.",
            Instructions: skillMarkdown.Trim(),
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            Files:
            [
                new FileUploadDto(
                    "Skill",
                    null,
                    "Skills/searxng-search/scripts/searxng.sh",
                    Encoding.UTF8.GetBytes(scriptContent),
                    "application/x-sh"),
            ],
            ConversationStarters: null);

        var created = await service.CreateAssistantAsync(dto);

        var persistedFiles = await context.AssistantFiles
            .Where(f => f.AssistantId == created.Id && f.FolderKind == "Skill")
            .ToListAsync();
        persistedFiles.Should().ContainSingle();
        persistedFiles[0].RelativePath.Should().Be("Skills/searxng-search/scripts/searxng.sh");
        Encoding.UTF8.GetString(persistedFiles[0].ContentBytes!).Should().Be(scriptContent);
        persistedFiles.Should().NotContain(f => f.RelativePath.EndsWith("SKILL.md"));

        (await context.AssistantSkillMetas.CountAsync(m => m.AssistantId == created.Id)).Should().Be(0);

        var assistant = await context.Assistants
            .Include(a => a.Files)
            .SingleAsync(a => a.Id == created.Id);
        assistant.Instructions.Should().Contain("SearXNG Search");

        var guideSkills = SkillDtoBuilder.BuildFromAssistantFiles(assistant.Files, assistant.SkillMetas);
        guideSkills.Should().BeEmpty();

        var filesContextOption = await context.AssistantContextOptions
            .SingleAsync(o => o.AssistantId == created.Id);
        filesContextOption.Key.Should().Be(GuideExecutablePayload.FilesContextOptionKey);
        filesContextOption.Value.Should().Be(GuideExecutablePayload.FilesContextOptionValue);
    }
}
