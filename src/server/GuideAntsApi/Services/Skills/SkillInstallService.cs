using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides.Skills;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Skills;

public sealed class SkillInstallService(ApplicationDbContext context) : ISkillInstallService
{
    private const string SkillFolderKind = "Skill";

    private readonly ApplicationDbContext _context = context;

    public sealed class SkillConflictException(string skillName)
        : InvalidOperationException($"Skill '{skillName}' already exists on this assistant.")
    {
        public string SkillName { get; } = skillName;
    }

    public async Task<AssistantSkillDto> InstallAsync(
        Guid assistantId,
        SkillInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SkillName))
        {
            throw new ArgumentException("Catalog skill name is required.", nameof(request));
        }

        var assistant = await _context.Assistants
            .FirstOrDefaultAsync(a => a.Id == assistantId, cancellationToken)
            ?? throw new InvalidOperationException($"Assistant '{assistantId}' was not found.");

        var (skill, snapshot) = await ResolveSnapshotAsync(request.SkillName, request.Version, cancellationToken);
        var manifest = snapshot.Files.FirstOrDefault(f =>
            f.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Catalog skill '{skill.Name}' version {snapshot.Version} has no SKILL.md.");

        var existingRows = await LoadSkillFileRowsAsync(assistantId, skill.Name, cancellationToken);
        var existingMeta = await _context.AssistantSkillMetas
            .FirstOrDefaultAsync(m => m.AssistantId == assistantId && m.SkillName == skill.Name, cancellationToken);

        if (existingRows.Count > 0 && existingMeta?.SkillId is null && !request.Overwrite)
        {
            throw new SkillConflictException(skill.Name);
        }

        var nextDisplayOrder = existingMeta?.DisplayOrder
            ?? await NextDisplayOrderAsync(assistantId, cancellationToken);

        ReplaceFiles(snapshot, assistantId, skill.Name, existingRows);
        await _context.SaveChangesAsync(cancellationToken);
        await SyncPinAsync(assistantId, skill, snapshot, manifest, nextDisplayOrder, cancellationToken);

        return await BuildDtoAsync(assistantId, skill.Name, cancellationToken);
    }

    public async Task<AssistantSkillDto> UpdateFromCatalogAsync(
        Guid assistantId,
        string skillName,
        SkillUpdateFromStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name is required.", nameof(skillName));
        }

        var meta = await _context.AssistantSkillMetas
            .FirstOrDefaultAsync(m => m.AssistantId == assistantId && m.SkillName == skillName, cancellationToken)
            ?? throw new InvalidOperationException($"Skill '{skillName}' was not found on this assistant.");
        if (meta.SkillId is null)
        {
            throw new InvalidOperationException(
                $"'{skillName}' is not installed from the skill catalog, so there is nothing to update.");
        }

        var skill = await _context.Skills
            .FirstOrDefaultAsync(s => s.Id == meta.SkillId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The catalog entry for '{skillName}' no longer exists.");

        var (resolved, snapshot) = await ResolveSnapshotAsync(skill.Name, request?.Version, cancellationToken);
        var manifest = snapshot.Files.FirstOrDefault(f =>
            f.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Catalog skill '{resolved.Name}' version {snapshot.Version} has no SKILL.md.");

        var existingRows = await LoadSkillFileRowsAsync(assistantId, skill.Name, cancellationToken);
        ReplaceFiles(snapshot, assistantId, skill.Name, existingRows);
        await _context.SaveChangesAsync(cancellationToken);
        await SyncPinAsync(assistantId, resolved, snapshot, manifest, meta.DisplayOrder, cancellationToken);

        return await BuildDtoAsync(assistantId, skill.Name, cancellationToken);
    }

    private async Task<(Skill Skill, SkillVersion Snapshot)> ResolveSnapshotAsync(
        string skillName,
        int? version,
        CancellationToken cancellationToken)
    {
        var skill = await _context.Skills
            .Include(s => s.Versions)
            .ThenInclude(v => v.Files)
            .FirstOrDefaultAsync(s => s.Name == skillName, cancellationToken)
            ?? throw new InvalidOperationException($"Skill '{skillName}' is not in the catalog.");

        var snapshot = version is null
            ? skill.CurrentSnapshot()
            : skill.Versions.FirstOrDefault(v => v.Version == version);

        return snapshot is null
            ? throw new InvalidOperationException(version is null
                ? $"Skill '{skillName}' has no published versions."
                : $"Skill '{skillName}' has no version {version}.")
            : (skill, snapshot);
    }

    /// <summary>Deletes the assistant's current rows for the skill and inserts the snapshot's
    /// files under the assistant convention Skills/&lt;name&gt;/&lt;packagePath&gt;.</summary>
    private void ReplaceFiles(
        SkillVersion snapshot,
        Guid assistantId,
        string skillName,
        List<AssistantFile> existingRows)
    {
        if (existingRows.Count > 0)
        {
            _context.AssistantFiles.RemoveRange(existingRows);
        }

        var now = DateTime.UtcNow;
        foreach (var file in snapshot.Files)
        {
            _context.AssistantFiles.Add(new AssistantFile
            {
                AssistantId = assistantId,
                FolderKind = SkillFolderKind,
                RelativePath = $"Skills/{skillName}/{file.RelativePath}",
                ContentBytes = file.ContentBytes,
                ContentType = file.ContentType,
                Created = now,
            });
        }
    }

    private async Task SyncPinAsync(
        Guid assistantId,
        Skill skill,
        SkillVersion snapshot,
        SkillFile manifest,
        int displayOrder,
        CancellationToken cancellationToken)
    {
        var frontmatter = SkillFrontmatter.Parse(
            System.Text.Encoding.UTF8.GetString(manifest.ContentBytes!));
        var hash = SkillContentHash.Compute(manifest.ContentBytes!);
        var now = DateTime.UtcNow;

        var meta = await _context.AssistantSkillMetas
            .FirstOrDefaultAsync(
                m => m.AssistantId == assistantId && m.SkillName == frontmatter.Name,
                cancellationToken);

        if (meta is null)
        {
            meta = new AssistantSkillMeta
            {
                AssistantId = assistantId,
                SkillName = frontmatter.Name,
                Created = now,
            };
            _context.AssistantSkillMetas.Add(meta);
        }

        meta.Description = frontmatter.Description;
        meta.Enabled = frontmatter.Enabled;
        meta.DisplayOrder = displayOrder;
        meta.ContentHash = hash;
        meta.SkillId = skill.Id;
        meta.CatalogVersion = snapshot.Version;
        meta.Updated = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<AssistantFile>> LoadSkillFileRowsAsync(
        Guid assistantId,
        string skillName,
        CancellationToken cancellationToken)
    {
        var folderKey = $"skills/{skillName}".ToLowerInvariant();
        var rows = await _context.AssistantFiles
            .Where(f => f.AssistantId == assistantId && f.FolderKind == SkillFolderKind)
            .ToListAsync(cancellationToken);

        return rows
            .Where(f => string.Equals(
                SkillPathSafety.SkillFolderKey(f.RelativePath)?.ToLowerInvariant(),
                folderKey,
                StringComparison.Ordinal))
            .ToList();
    }

    private async Task<int> NextDisplayOrderAsync(
        Guid assistantId,
        CancellationToken cancellationToken)
    {
        var orders = await _context.AssistantSkillMetas
            .Where(m => m.AssistantId == assistantId)
            .Select(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);
        return orders.Count == 0 ? 0 : orders.Max() + 1;
    }

    private async Task<AssistantSkillDto> BuildDtoAsync(
        Guid assistantId,
        string skillName,
        CancellationToken cancellationToken)
    {
        var files = await _context.AssistantFiles
            .Where(f => f.AssistantId == assistantId && f.FolderKind == SkillFolderKind)
            .ToListAsync(cancellationToken);
        var metas = await _context.AssistantSkillMetas
            .Where(m => m.AssistantId == assistantId)
            .ToListAsync(cancellationToken);

        var catalogVersions = await CatalogVersionLookup.ResolveForAsync(_context, metas, cancellationToken);
        var skills = SkillDtoBuilder.BuildFromAssistantFiles(
            files,
            metas,
            catalogCurrentVersions: catalogVersions);
        return skills.FirstOrDefault(s => string.Equals(
                s.Name,
                skillName,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Skill '{skillName}' could not be read back after install.");
    }
}
