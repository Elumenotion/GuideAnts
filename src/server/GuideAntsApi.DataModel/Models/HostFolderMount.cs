using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

[Index(nameof(ProjectId), nameof(Status))]
[Index(nameof(MountKey), IsUnique = true)]
public class HostFolderMount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public Guid? NotebookId { get; set; }

    public Notebook? Notebook { get; set; }

    [Required]
    public HostFolderMountScope Scope { get; set; }

    [Required]
    public SourceKind SourceKind { get; set; }

    [Required]
    [StringLength(255)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string LeafName { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string MountKey { get; set; } = string.Empty;

    /// <summary>
    /// LocalPath: absolute host path. Smb: display/UNC form. Admin-only sensitive.
    /// </summary>
    [Required]
    [AdminOnlySensitive]
    public string SourceSpec { get; set; } = string.Empty;

    [Required]
    [StringLength(512)]
    public string ContainerSourcePath { get; set; } = string.Empty;

    /// <summary>Smb only: CIFS device (e.g. //server/share/sub). Null for LocalPath.</summary>
    [StringLength(1024)]
    public string? NetworkDevice { get; set; }

    /// <summary>Smb only: non-secret mount options (uid, gid, file_mode, etc.). Null for LocalPath.</summary>
    [StringLength(2048)]
    public string? NetworkOptions { get; set; }

    /// <summary>Smb only: secret-store reference — never raw credentials. Admin-only sensitive.</summary>
    [StringLength(512)]
    [AdminOnlySensitive]
    public string? CredentialRef { get; set; }

    [Required]
    public HostFolderMountStatus Status { get; set; } = HostFolderMountStatus.PendingRestart;

    [Required]
    public Guid CreatedByUserId { get; set; }

    public User CreatedByUser { get; set; } = null!;

    [Required]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RemovedUtc { get; set; }

    [StringLength(4000)]
    public string? ErrorMessage { get; set; }

    public ICollection<HostFolderMountLink> Links { get; set; } = [];
}
