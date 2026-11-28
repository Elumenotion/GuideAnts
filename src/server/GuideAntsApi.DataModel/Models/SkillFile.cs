using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// One file inside a <see cref="SkillVersion"/> snapshot. RelativePath mirrors the assistant
/// convention without the Skills/&lt;name&gt;/ prefix, e.g. "SKILL.md", "references/setup.md".
/// </summary>
public class SkillFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public Guid SkillVersionId { get; set; }

    [ForeignKey(nameof(SkillVersionId))]
    public SkillVersion SkillVersion { get; set; } = null!;

    [Required]
    [StringLength(1024)]
    public string RelativePath { get; set; } = string.Empty;

    public byte[]? ContentBytes { get; set; }

    [StringLength(128)]
    public string? ContentType { get; set; }

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;
}
