using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Immutable snapshot of a published skill package. Never edited in place: republishing creates
/// the next <see cref="Version"/>. Installs pin a specific snapshot.
/// </summary>
public class SkillVersion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public Guid SkillId { get; set; }

    [ForeignKey(nameof(SkillId))]
    public Skill Skill { get; set; } = null!;

    /// <summary>Monotonic per skill, starting at 1.</summary>
    [Required]
    public int Version { get; set; }

    /// <summary>SHA-256 hex of the canonical SKILL.md bytes, same definition as
    /// <see cref="AssistantSkillMeta.ContentHash"/>.</summary>
    [Required]
    [StringLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    [Required]
    [StringLength(1024)]
    public string Description { get; set; } = string.Empty;

    // Gating frontmatter cached so the builder can evaluate prerequisites before installing.
    [Required] public string RequiresToolsetsJson { get; set; } = "[]";
    [Required] public string RequiresToolsJson { get; set; } = "[]";
    [Required] public string FallbackForToolsetsJson { get; set; } = "[]";
    [Required] public string FallbackForToolsJson { get; set; } = "[]";
    [Required] public string PlatformsJson { get; set; } = "[]";

    [Required]
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SkillFile> Files { get; set; } = [];
}
