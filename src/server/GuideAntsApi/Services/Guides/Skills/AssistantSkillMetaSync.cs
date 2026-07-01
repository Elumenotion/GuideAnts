using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Guides.Skills;

public sealed class AssistantSkillMetaSync(ApplicationDbContext context) : IAssistantSkillMetaSync
{
    private readonly ApplicationDbContext _context = context;

    public static string ComputeContentHash(byte[] contentBytes) =>
        SkillContentHash.Compute(contentBytes);

    public async Task SyncAssistantAsync(Guid assistantId, CancellationToken cancellationToken = default)
    {
        var skillFiles = await _context.AssistantFiles
            .Where(f => f.AssistantId == assistantId && f.FolderKind == "Skill")
            .ToListAsync(cancellationToken);

        var existing = await _context.AssistantSkillMetas
            .Where(m => m.AssistantId == assistantId)
            .ToListAsync(cancellationToken);

        if (skillFiles.Count == 0)
        {
            if (existing.Count > 0)
            {
                _context.AssistantSkillMetas.RemoveRange(existing);
                await _context.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var group in skillFiles
                     .GroupBy(f => SkillPathSafety.SkillFolderKey(f.RelativePath))
                     .Where(g => g.Key is not null))
        {
            var manifest = group.FirstOrDefault(f =>
                f.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (manifest?.ContentBytes is null)
            {
                continue;
            }

            var frontmatter = SkillFrontmatter.Parse(Encoding.UTF8.GetString(manifest.ContentBytes));
            var hash = ComputeContentHash(manifest.ContentBytes);
            skillNames.Add(frontmatter.Name);

            var row = existing.FirstOrDefault(m =>
                string.Equals(m.SkillName, frontmatter.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                _context.AssistantSkillMetas.Add(new AssistantSkillMeta
                {
                    AssistantId = assistantId,
                    SkillName = frontmatter.Name,
                    Description = frontmatter.Description,
                    Enabled = frontmatter.Enabled,
                    DisplayOrder = frontmatter.DisplayOrder,
                    ContentHash = hash,
                    Created = now,
                    Updated = now,
                });
            }
            else
            {
                row.Description = frontmatter.Description;
                row.Enabled = frontmatter.Enabled;
                row.DisplayOrder = frontmatter.DisplayOrder;
                row.ContentHash = hash;
                row.Updated = now;
            }
        }

        var orphans = existing.Where(m => !skillNames.Contains(m.SkillName)).ToList();
        if (orphans.Count > 0)
        {
            _context.AssistantSkillMetas.RemoveRange(orphans);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SyncFromSkillSavesAsync(
        Guid assistantId,
        IReadOnlyList<AssistantSkillSaveDto> skills,
        CancellationToken cancellationToken = default)
    {
        if (skills is not { Count: > 0 })
        {
            await SyncAssistantAsync(assistantId, cancellationToken);
            return;
        }

        var skillFiles = await _context.AssistantFiles
            .Where(f => f.AssistantId == assistantId && f.FolderKind == "Skill")
            .ToListAsync(cancellationToken);

        var savesByManifestId = new Dictionary<Guid, AssistantSkillSaveDto>();
        foreach (var skill in skills)
        {
            if (skill.FileIdsToKeep is not { Count: > 0 })
            {
                continue;
            }

            foreach (var fileId in skill.FileIdsToKeep)
            {
                var manifest = skillFiles.FirstOrDefault(f =>
                    f.Id == fileId
                    && f.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
                if (manifest is not null)
                {
                    savesByManifestId[manifest.Id] = skill;
                    break;
                }
            }
        }

        var existing = await _context.AssistantSkillMetas
            .Where(m => m.AssistantId == assistantId)
            .ToListAsync(cancellationToken);

        var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var group in skillFiles
                     .GroupBy(f => SkillPathSafety.SkillFolderKey(f.RelativePath))
                     .Where(g => g.Key is not null))
        {
            var manifest = group.FirstOrDefault(f =>
                f.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (manifest?.ContentBytes is null)
            {
                continue;
            }

            var frontmatter = SkillFrontmatter.Parse(Encoding.UTF8.GetString(manifest.ContentBytes));
            var hash = ComputeContentHash(manifest.ContentBytes);
            skillNames.Add(frontmatter.Name);

            var enabled = frontmatter.Enabled;
            var displayOrder = frontmatter.DisplayOrder;
            if (savesByManifestId.TryGetValue(manifest.Id, out var save))
            {
                enabled = save.Enabled;
                displayOrder = save.DisplayOrder;
            }

            var row = existing.FirstOrDefault(m =>
                string.Equals(m.SkillName, frontmatter.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                _context.AssistantSkillMetas.Add(new AssistantSkillMeta
                {
                    AssistantId = assistantId,
                    SkillName = frontmatter.Name,
                    Description = frontmatter.Description,
                    Enabled = enabled,
                    DisplayOrder = displayOrder,
                    ContentHash = hash,
                    Created = now,
                    Updated = now,
                });
            }
            else
            {
                row.Description = frontmatter.Description;
                row.Enabled = enabled;
                row.DisplayOrder = displayOrder;
                row.ContentHash = hash;
                row.Updated = now;
            }
        }

        var orphans = existing.Where(m => !skillNames.Contains(m.SkillName)).ToList();
        if (orphans.Count > 0)
        {
            _context.AssistantSkillMetas.RemoveRange(orphans);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertFromManifestAsync(
        Guid assistantId,
        AssistantFile manifestFile,
        CancellationToken cancellationToken = default)
    {
        if (manifestFile.ContentBytes is null)
        {
            throw new InvalidOperationException(
                $"Skill manifest '{manifestFile.RelativePath}' has no content.");
        }

        var frontmatter = SkillFrontmatter.Parse(Encoding.UTF8.GetString(manifestFile.ContentBytes));
        var hash = ComputeContentHash(manifestFile.ContentBytes);
        var now = DateTime.UtcNow;

        var row = await _context.AssistantSkillMetas
            .FirstOrDefaultAsync(
                m => m.AssistantId == assistantId
                     && m.SkillName == frontmatter.Name,
                cancellationToken);

        if (row is null)
        {
            _context.AssistantSkillMetas.Add(new AssistantSkillMeta
            {
                AssistantId = assistantId,
                SkillName = frontmatter.Name,
                Description = frontmatter.Description,
                Enabled = frontmatter.Enabled,
                DisplayOrder = frontmatter.DisplayOrder,
                ContentHash = hash,
                Created = now,
                Updated = now,
            });
        }
        else
        {
            row.Description = frontmatter.Description;
            row.Enabled = frontmatter.Enabled;
            row.DisplayOrder = frontmatter.DisplayOrder;
            row.ContentHash = hash;
            row.Updated = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
