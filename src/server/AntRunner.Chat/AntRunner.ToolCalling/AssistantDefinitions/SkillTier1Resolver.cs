using System.Text;
using GuideAntsApi.DataModel.Models;

namespace AntRunner.ToolCalling.AssistantDefinitions;

public readonly record struct SkillTier1Fields(
    string Name,
    string Description,
    bool Enabled,
    int DisplayOrder,
    bool UsedSidecar);

/// <summary>
/// Resolves tier-1 skill metadata, preferring the sidecar when its content hash matches
/// the current <c>SKILL.md</c> bytes. Stale sidecar rows are ignored for model/UI output.
/// </summary>
public static class SkillTier1Resolver
{
    public static SkillTier1Fields Resolve(byte[] manifestBytes, AssistantSkillMeta? meta)
    {
        var hash = SkillContentHash.Compute(manifestBytes);
        var frontmatter = SkillFrontmatter.Parse(Encoding.UTF8.GetString(manifestBytes));

        if (meta is not null
            && string.Equals(meta.SkillName, frontmatter.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(meta.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            return new SkillTier1Fields(
                meta.SkillName,
                meta.Description,
                meta.Enabled,
                meta.DisplayOrder,
                UsedSidecar: true);
        }

        return new SkillTier1Fields(
            frontmatter.Name,
            frontmatter.Description,
            frontmatter.Enabled,
            frontmatter.DisplayOrder,
            UsedSidecar: false);
    }
}
