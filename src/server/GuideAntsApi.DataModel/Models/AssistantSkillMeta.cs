using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Metadata-only sidecar for assistant skills. Caches tier-1 listing fields so the UI and
/// materializer avoid re-parsing <c>SKILL.md</c> blobs. Bodies remain in <see cref="AssistantFile"/>.
/// </summary>
public class AssistantSkillMeta
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public Guid AssistantId { get; set; }

    [ForeignKey(nameof(AssistantId))]
    public Assistant Assistant { get; set; } = null!;

    [Required]
    [StringLength(128)]
    public string SkillName { get; set; } = string.Empty;

    [Required]
    [StringLength(1024)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public bool Enabled { get; set; } = true;

    [Required]
    public int DisplayOrder { get; set; }

    /// <summary>SHA-256 hex of the canonical <c>SKILL.md</c> bytes.</summary>
    [Required]
    [StringLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? Updated { get; set; }
}
