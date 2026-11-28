using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// A skill in the global skill catalog. Catalog entries exist independently of assistants:
/// identity lives here, immutable payloads live in <see cref="SkillVersion"/> snapshots and
/// their <see cref="SkillFile"/> rows.
/// </summary>
public class Skill
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>Store-wide unique skill name (matches SKILL.md frontmatter 'name').</summary>
    [Required]
    [StringLength(128)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(1024)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Latest published snapshot. Deliberately a plain column with no foreign key: a pointer
    /// column here plus the Skill -> SkillVersion cascade is a cascade cycle SQL Server rejects.
    /// The publish service maintains it and readers resolve it through <see cref="Versions"/>.
    /// </summary>
    public Guid? CurrentVersionId { get; set; }

    /// <summary>Provenance only: assistant the skill was published from. No FK constraint, so
    /// deleting a guide/assistant never reaches into the catalog.</summary>
    public Guid? PublishedFromAssistantId { get; set; }

    [StringLength(128)]
    public string? PublishedFromSkillName { get; set; }

    /// <summary>Reserved for per-owner catalog scoping; v1 entries are always global.</summary>
    [Required]
    public bool IsGlobal { get; set; } = true;

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? Updated { get; set; }

    public ICollection<SkillVersion> Versions { get; set; } = [];
}
