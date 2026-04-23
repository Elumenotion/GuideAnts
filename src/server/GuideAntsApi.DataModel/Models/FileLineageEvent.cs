using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Immutable audit record that captures who performed what action on which file (and version) when.
/// </summary>
public class FileLineageEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Azure AD Object Id of the user that triggered the action.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public FileLineageAction Action { get; set; }

    [Required]
    public FileKind FileKind { get; set; }

    /// <summary>
    /// Identifier of the logical file (ContentFile or NotebookFile).
    /// </summary>
    [Required]
    public Guid FileId { get; set; }

    /// <summary>
    /// Version number when the lineage event pertains to a specific version of a project file.
    /// Null for notebook files.
    /// </summary>
    public int? VersionNumber { get; set; }

    [Required]
    public Guid ProjectId { get; set; }

    /// <summary>
    /// Identifier of the notebook when <see cref="FileKind"/> is Notebook.
    /// </summary>
    public Guid? NotebookId { get; set; }

    /// <summary>
    /// Materialised absolute storage path for convenience; may be null if resolvable by joins.
    /// </summary>
    [StringLength(512)]
    public string? StoragePath { get; set; }

    [Required]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
} 