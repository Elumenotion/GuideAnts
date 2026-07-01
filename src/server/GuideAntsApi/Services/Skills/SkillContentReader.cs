using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Skills;

/// <summary>
/// Shared on-demand skill body and reference loading with path-safety.
/// </summary>
public static class SkillContentReader
{
    public sealed record SkillReadSuccess(string RelativePath, string Content);

    public static async Task<SkillReadSuccess?> TryReadAsync(
        ApplicationDbContext db,
        Guid assistantId,
        SkillDescriptor descriptor,
        string? filePath,
        CancellationToken cancellationToken = default)
    {
        string relativePath;
        try
        {
            relativePath = SkillPathSafety.ResolveUnderSkillFolder(descriptor.FolderPath, filePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!descriptor.Files.Any(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var bytes = await ReadSkillFileBytesAsync(db, assistantId, relativePath, cancellationToken);
        if (bytes == null)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        var isSkillManifest = string.Equals(
            SkillPathSafety.NormalizePath(relativePath),
            SkillPathSafety.NormalizePath($"{descriptor.FolderPath}/SKILL.md"),
            StringComparison.OrdinalIgnoreCase);

        if (isSkillManifest && string.IsNullOrWhiteSpace(filePath))
        {
            text = SkillFrontmatter.ExtractBody(text);
        }

        return new SkillReadSuccess(relativePath, text);
    }

    private static async Task<byte[]?> ReadSkillFileBytesAsync(
        ApplicationDbContext db,
        Guid assistantId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var files = await db.AssistantFiles
            .AsNoTracking()
            .Where(f => f.AssistantId == assistantId && f.FolderKind == "Skill")
            .ToListAsync(cancellationToken);

        var match = files.FirstOrDefault(f =>
            string.Equals(SkillPathSafety.NormalizePath(f.RelativePath), relativePath, StringComparison.OrdinalIgnoreCase));

        return match?.ContentBytes;
    }
}
