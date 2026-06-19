using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

[Index(nameof(HostFolderMountId), nameof(NotebookId), IsUnique = true)]
public class HostFolderMountLink
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid HostFolderMountId { get; set; }

    public HostFolderMount HostFolderMount { get; set; } = null!;

    [Required]
    public Guid NotebookId { get; set; }

    public Notebook Notebook { get; set; } = null!;

    /// <summary>Leaf folder name relative to the notebook root (plan §9 collision key).</summary>
    [Required]
    [StringLength(255)]
    public string LinkRelativePath { get; set; } = string.Empty;

    [Required]
    [StringLength(1024)]
    public string LinkPhysicalPath { get; set; } = string.Empty;

    [Required]
    public HostFolderMountLinkStatus Status { get; set; } = HostFolderMountLinkStatus.PendingRestart;

    public DateTime? LastLinkedUtc { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    [StringLength(4000)]
    public string? ErrorMessage { get; set; }
}
