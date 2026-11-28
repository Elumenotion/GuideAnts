using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
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
        var catalogHashes = await LoadCatalogHashesAsync(existing, cancellationToken);

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
                ApplyContentHashWithFork(row, hash, catalogHashes);
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
        var savesByName = new Dictionary<string, AssistantSkillSaveDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            if (!string.IsNullOrWhiteSpace(skill.Name))
            {
                savesByName[skill.Name] = skill;
            }

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
        var catalogHashes = await LoadCatalogHashesAsync(existing, cancellationToken);

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
            if (savesByManifestId.TryGetValue(manifest.Id, out var save)
                || savesByName.TryGetValue(frontmatter.Name, out save))
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
                ApplyContentHashWithFork(row, hash, catalogHashes);
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
        var catalogHashes = await LoadCatalogHashesAsync(
            row is null ? [] : [row],
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
            ApplyContentHashWithFork(row, hash, catalogHashes);
            row.Updated = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Loads the SKILL.md hash of each pinned catalog snapshot, keyed "SkillId:Version", so the
    /// fork decision needs one query instead of one per skill.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadCatalogHashesAsync(
        List<AssistantSkillMeta> metas,
        CancellationToken cancellationToken)
    {
        var pinned = metas.Where(m => m.SkillId is not null && m.CatalogVersion is not null).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pinned.Count == 0)
        {
            return result;
        }

        var skillIds = pinned.Select(m => m.SkillId!.Value).Distinct().ToList();
        var versions = pinned.Select(m => m.CatalogVersion!.Value).Distinct().ToList();

        var rows = await _context.SkillVersions
            .AsNoTracking()
            .Where(v => skillIds.Contains(v.SkillId) && versions.Contains(v.Version))
            .Select(v => new { v.SkillId, v.Version, v.ContentHash })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            result[$"{row.SkillId}:{row.Version}"] = row.ContentHash;
        }

        return result;
    }

    /// <summary>
    /// Records the local SKILL.md hash and applies the fork rule: a catalog-pinned install keeps
    /// its pin only while the local bytes still match the pinned snapshot. Any local edit (or a
    /// vanished snapshot) detaches the pin so the skill is treated as a plain local package.
    /// </summary>
    private static void ApplyContentHashWithFork(
        AssistantSkillMeta row,
        string hash,
        Dictionary<string, string> catalogHashes)
    {
        row.ContentHash = hash;

        if (row.SkillId is null || row.CatalogVersion is null)
        {
            return;
        }

        var key = $"{row.SkillId}:{row.CatalogVersion}";
        if (!catalogHashes.TryGetValue(key, out var snapshotHash)
            || !string.Equals(snapshotHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            row.SkillId = null;
            row.CatalogVersion = null;
        }
    }
}
