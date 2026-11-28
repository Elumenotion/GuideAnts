using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Skills;

public sealed class SkillCatalogService(ApplicationDbContext context) : ISkillCatalogService
{
    private const string SkillFolderKind = "Skill";

    private readonly ApplicationDbContext _context = context;

    public async Task<IReadOnlyList<SkillCatalogEntryDto>> ListAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Skills.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => s.Name.Contains(term) || s.Description.Contains(term));
        }

        var rows = await query
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.CurrentVersionId,
                s.Updated,
                s.Created,
                Versions = s.Versions
                    .Select(v => new { v.Id, v.Version, FileCount = v.Files.Count })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r =>
            {
                var current = r.Versions.FirstOrDefault(v => v.Id == r.CurrentVersionId)
                    ?? r.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
                return new SkillCatalogEntryDto(
                    r.Id,
                    r.Name,
                    r.Description,
                    current?.Version ?? 0,
                    current?.FileCount ?? 0,
                    r.Updated ?? r.Created);
            })
            .ToList();
    }

    public async Task<SkillCatalogDetailDto?> GetAsync(
        string skillName,
        int? version,
        CancellationToken cancellationToken = default)
    {
        var skill = await _context.Skills
            .AsNoTracking()
            .Include(s => s.Versions)
            .ThenInclude(v => v.Files)
            .FirstOrDefaultAsync(s => s.Name == skillName, cancellationToken);
        if (skill is null)
        {
            return null;
        }

        var snapshot = version is null
            ? skill.CurrentSnapshot()
            : skill.Versions.FirstOrDefault(v => v.Version == version);
        if (snapshot is null)
        {
            return null;
        }

        return new SkillCatalogDetailDto(
            skill.Id,
            skill.Name,
            snapshot.Description,
            snapshot.Version,
            snapshot.ContentHash,
            ReadList(snapshot.RequiresToolsetsJson),
            ReadList(snapshot.RequiresToolsJson),
            ReadList(snapshot.FallbackForToolsetsJson),
            ReadList(snapshot.FallbackForToolsJson),
            ReadList(snapshot.PlatformsJson),
            snapshot.Files
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(f => new SkillCatalogFileDto(
                    f.RelativePath,
                    f.ContentType,
                    f.ContentBytes?.Length ?? 0))
                .ToList(),
            skill.Versions.Select(v => v.Version).OrderByDescending(v => v).ToList(),
            skill.Updated ?? skill.Created);
    }

    public async Task<SkillCatalogPackageDto?> GetPackageAsync(
        string skillName,
        int? version,
        CancellationToken cancellationToken = default)
    {
        var skill = await _context.Skills
            .AsNoTracking()
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.Name == skillName, cancellationToken);
        if (skill is null)
        {
            return null;
        }

        var snapshot = version is null
            ? skill.CurrentSnapshot()
            : skill.Versions.FirstOrDefault(v => v.Version == version);
        if (snapshot is null)
        {
            return null;
        }

        var files = await _context.SkillFiles
            .AsNoTracking()
            .Where(f => f.SkillVersionId == snapshot.Id)
            .OrderBy(f => f.RelativePath)
            .ToListAsync(cancellationToken);

        var payload = files
            .Select(f => new SkillCatalogPackageFileDto(
                f.RelativePath,
                f.ContentType,
                Convert.ToBase64String(f.ContentBytes ?? Array.Empty<byte>())))
            .ToList();

        return new SkillCatalogPackageDto(skill.Name, snapshot.Version, payload);
    }

    public async Task<SkillPublishResultDto> PublishFromAssistantAsync(
        Guid assistantId,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name is required.", nameof(skillName));
        }

        var assistant = await _context.Assistants
            .FirstOrDefaultAsync(a => a.Id == assistantId, cancellationToken)
            ?? throw new InvalidOperationException($"Assistant '{assistantId}' was not found.");

        var package = await LoadAssistantSkillPackageAsync(assistantId, skillName, cancellationToken);
        var manifest = package.Manifest
            ?? throw new InvalidOperationException(
                $"Skill '{skillName}' has no SKILL.md on assistant '{assistant.Name}'.");

        var frontmatter = SkillFrontmatter.Parse(System.Text.Encoding.UTF8.GetString(manifest.ContentBytes!));
        if (!string.Equals(frontmatter.Name, skillName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Skill folder '{skillName}' declares frontmatter name '{frontmatter.Name}'.");
        }

        var contentHash = SkillContentHash.Compute(manifest.ContentBytes!);

        var skill = await _context.Skills
            .FirstOrDefaultAsync(s => s.Name == frontmatter.Name, cancellationToken);

        var createdNew = skill is null;
        if (skill is null)
        {
            skill = new Skill
            {
                Name = frontmatter.Name,
                Description = frontmatter.Description,
                Created = DateTime.UtcNow,
            };
            _context.Skills.Add(skill);
        }

        var highest = await _context.SkillVersions
            .Where(v => v.SkillId == skill.Id)
            .Select(v => (int?)v.Version)
            .MaxAsync(cancellationToken);
        var nextVersion = (highest ?? 0) + 1;
        var snapshot = new SkillVersion
        {
            Skill = skill,
            Version = nextVersion,
            ContentHash = contentHash,
            Description = frontmatter.Description,
            RequiresToolsetsJson = WriteList(frontmatter.RequiresToolsets),
            RequiresToolsJson = WriteList(frontmatter.RequiresTools),
            FallbackForToolsetsJson = WriteList(frontmatter.FallbackForToolsets),
            FallbackForToolsJson = WriteList(frontmatter.FallbackForTools),
            PlatformsJson = WriteList(frontmatter.Platforms),
            PublishedAt = DateTime.UtcNow,
        };

        foreach (var file in package.Files)
        {
            snapshot.Files.Add(new SkillFile
            {
                RelativePath = StripSkillFolder(file.RelativePath),
                ContentBytes = file.ContentBytes,
                ContentType = file.ContentType,
            });
        }

        _context.SkillVersions.Add(snapshot);
        await _context.SaveChangesAsync(cancellationToken);

        skill.CurrentVersionId = snapshot.Id;
        skill.Description = frontmatter.Description;
        skill.PublishedFromAssistantId = assistantId;
        skill.PublishedFromSkillName = skillName;
        skill.Updated = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new SkillPublishResultDto(skill.Id, skill.Name, snapshot.Version, createdNew);
    }

    public async Task<int?> DeleteAsync(string skillName, CancellationToken cancellationToken = default)
    {
        var skill = await _context.Skills
            .FirstOrDefaultAsync(s => s.Name == skillName, cancellationToken);
        if (skill is null)
        {
            return null;
        }

        // Installs keep working as plain local skills once the catalog entry is gone.
        var installs = await _context.AssistantSkillMetas
            .Where(m => m.SkillId == skill.Id)
            .ToListAsync(cancellationToken);
        foreach (var install in installs)
        {
            install.SkillId = null;
            install.CatalogVersion = null;
        }

        // Remove the snapshot tree explicitly, deepest first. Cascade deletes are a database
        // behaviour the in-memory provider does not simulate, and relying on it would let
        // SkillFiles survive a catalog delete in tests while disappearing in production.
        var versionIds = await _context.SkillVersions
            .Where(v => v.SkillId == skill.Id)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);
        if (versionIds.Count > 0)
        {
            _context.SkillFiles.RemoveRange(
                _context.SkillFiles.Where(f => versionIds.Contains(f.SkillVersionId)));
            _context.SkillVersions.RemoveRange(
                _context.SkillVersions.Where(v => versionIds.Contains(v.Id)));
        }

        _context.Skills.Remove(skill);
        await _context.SaveChangesAsync(cancellationToken);
        return installs.Count;
    }

    private sealed record SkillPackage(AssistantFile? Manifest, List<AssistantFile> Files);

    /// <summary>Loads the assistant's saved skill package (the same rows the materializer uses).</summary>
    private async Task<SkillPackage> LoadAssistantSkillPackageAsync(
        Guid assistantId,
        string skillName,
        CancellationToken cancellationToken)
    {
        var files = await _context.AssistantFiles
            .Where(f => f.AssistantId == assistantId && f.FolderKind == SkillFolderKind)
            .ToListAsync(cancellationToken);

        var folderKey = $"skills/{skillName}".ToLowerInvariant();
        var package = files
            .Where(f => string.Equals(
                SkillPathSafety.SkillFolderKey(f.RelativePath)?.ToLowerInvariant(),
                folderKey,
                StringComparison.Ordinal))
            .ToList();

        if (package.Count == 0)
        {
            throw new InvalidOperationException($"Skill '{skillName}' was not found on this assistant.");
        }

        var manifest = package.FirstOrDefault(f =>
            f.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
        return new SkillPackage(manifest, package);
    }

    /// <summary>Drops the Skills/&lt;name&gt;/ prefix; catalog rows store package-relative paths.</summary>
    internal static string StripSkillFolder(string relativePath)
    {
        var normalized = SkillPathSafety.NormalizePath(relativePath);
        var parts = normalized.Split('/', 3);
        return parts.Length == 3 && parts[0].Equals("Skills", StringComparison.OrdinalIgnoreCase)
            ? parts[2]
            : normalized;
    }

    private static string WriteList(List<string> values) =>
        JsonSerializer.Serialize(values ?? []);

    private static List<string> ReadList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
