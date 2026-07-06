using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides.Skills;

internal static class SkillDtoBuilder
{
    public static List<AssistantSkillDto> BuildFromAssistantFiles(
        IEnumerable<AssistantFile> assistantFiles,
        IEnumerable<AssistantSkillMeta>? skillMetas = null,
        Func<AssistantFile, AssistantFileMarkdownShadowDto?>? shadowFactory = null)
    {
        var skillFiles = assistantFiles.Where(f => f.FolderKind == "Skill").ToList();
        if (skillFiles.Count == 0)
        {
            return [];
        }

        var metaByName = (skillMetas ?? [])
            .ToDictionary(m => m.SkillName, StringComparer.OrdinalIgnoreCase);

        var skills = new List<AssistantSkillDto>();
        foreach (var group in skillFiles.GroupBy(f => SkillPathSafety.SkillFolderKey(f.RelativePath))
                     .Where(g => g.Key is not null))
        {
            var manifest = group.FirstOrDefault(f =>
                f.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (manifest?.ContentBytes is null)
            {
                continue;
            }

            var markdown = Encoding.UTF8.GetString(manifest.ContentBytes);
            var frontmatter = SkillFrontmatter.Parse(markdown);
            metaByName.TryGetValue(frontmatter.Name, out var meta);
            var tier1 = SkillTier1Resolver.Resolve(manifest.ContentBytes, meta);
            var source = DetectSource(markdown);

            var files = group.Select(f => new FileDto(
                f.Id,
                f.FolderKind,
                f.VectorStoreName,
                f.RelativePath,
                f.ContentType,
                f.Created,
                shadowFactory?.Invoke(f))).ToList();

            skills.Add(new AssistantSkillDto(
                tier1.Name,
                tier1.Description,
                tier1.Enabled,
                tier1.DisplayOrder,
                source,
                files,
                frontmatter.RequiresToolsets,
                frontmatter.RequiresTools,
                frontmatter.FallbackForToolsets,
                frontmatter.FallbackForTools));
        }

        return skills
            .OrderBy(skill => skill.DisplayOrder)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<FileDto> BuildNonSkillFiles(
        IEnumerable<AssistantFile> assistantFiles,
        Func<AssistantFile, AssistantFileMarkdownShadowDto?> shadowFactory)
    {
        return assistantFiles
            .Where(f => !string.Equals(f.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileDto(
                f.Id,
                f.FolderKind,
                f.VectorStoreName,
                f.RelativePath,
                f.ContentType,
                f.Created,
                shadowFactory(f)))
            .ToList();
    }

    public static void ValidateSkillSavePayload(IReadOnlyList<AssistantSkillSaveDto>? skills)
    {
        if (skills is not { Count: > 0 })
        {
            return;
        }

        if (skills.Count > DatabaseStorage.MaxSkillsPerAssistant)
        {
            throw new InvalidOperationException(
                $"Cannot save more than {DatabaseStorage.MaxSkillsPerAssistant} skills per assistant.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            if (!names.Add(skill.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate skill name '{skill.Name}' is not allowed within an assistant.");
            }
        }
    }

    public static List<FileUploadDto> FlattenSkillUploads(IReadOnlyList<AssistantSkillSaveDto>? skills)
    {
        if (skills is not { Count: > 0 })
        {
            return [];
        }

        var uploads = new List<FileUploadDto>();
        foreach (var skill in skills)
        {
            if (skill.FilesToAdd is not { Count: > 0 })
            {
                continue;
            }

            foreach (var file in skill.FilesToAdd)
            {
                if (file.ContentBytes is not { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"Skill '{skill.Name}' file '{file.RelativePath}' has empty content.");
                }

                if (!string.Equals(file.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Skill '{skill.Name}' file '{file.RelativePath}' must use FolderKind 'Skill'.");
                }

                uploads.Add(file with { FolderKind = "Skill" });
            }
        }

        return uploads;
    }

    public static HashSet<Guid> CollectSkillFileIdsToKeep(IReadOnlyList<AssistantSkillSaveDto>? skills)
    {
        var ids = new HashSet<Guid>();
        if (skills is not { Count: > 0 })
        {
            return ids;
        }

        foreach (var skill in skills)
        {
            if (skill.FileIdsToKeep is not { Count: > 0 })
            {
                continue;
            }

            foreach (var id in skill.FileIdsToKeep)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string DetectSource(string markdown)
    {
        try
        {
            var frontmatter = SkillFrontmatter.Parse(markdown);
            _ = frontmatter;
        }
        catch (InvalidOperationException)
        {
            return "Imported";
        }

        if (markdown.Contains("source: authored", StringComparison.OrdinalIgnoreCase)
            || markdown.Contains("source: \"authored\"", StringComparison.OrdinalIgnoreCase))
        {
            return "Authored";
        }

        if (markdown.Contains("source: bootstrap", StringComparison.OrdinalIgnoreCase)
            || markdown.Contains("source: \"bootstrap\"", StringComparison.OrdinalIgnoreCase))
        {
            return "Bootstrap";
        }

        return "Imported";
    }
}
