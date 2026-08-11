using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Durable installation provenance for a catalog model. One-to-one with <see cref="Model"/>.
/// Operation logs and transient progress are stored separately in <see cref="LocalModelOperation"/>.
/// </summary>
public class LocalModelInstallation
{
    [Key]
    [StringLength(128)]
    public string ModelId { get; set; } = string.Empty;

    public Model Model { get; set; } = null!;

    [Required]
    [StringLength(32)]
    public string ManagementMode { get; set; } = string.Empty;

    [StringLength(128)]
    public string? CatalogId { get; set; }

    [StringLength(64)]
    public string? CatalogVersion { get; set; }

    [StringLength(512)]
    public string? Repository { get; set; }

    [StringLength(128)]
    public string? RequestedRevision { get; set; }

    [StringLength(128)]
    public string? ResolvedRevision { get; set; }

    [StringLength(64)]
    public string? QuantId { get; set; }

    [StringLength(64)]
    public string? QuantLabel { get; set; }

    [StringLength(128)]
    public string? RouterModelId { get; set; }

    [StringLength(512)]
    public string? TargetDirectory { get; set; }

    /// <summary>
    /// Ordered JSON array of model artifact records (repository path, installed path, size, digest).
    /// </summary>
    [Required]
    public string ModelArtifactsJson { get; set; } = "[]";

    /// <summary>
    /// Ordered JSON array of projector artifact records.
    /// </summary>
    [Required]
    public string ProjectorArtifactsJson { get; set; } = "[]";

    /// <summary>
    /// Ordered JSON array of companion artifact records (DFlash drafters, etc.).
    /// </summary>
    [Required]
    public string CompanionArtifactsJson { get; set; } = "[]";

    /// <summary>
    /// Complete alias preset snapshot written to router INI at install time.
    /// </summary>
    [Required]
    public string RouterPresetSnapshotJson { get; set; } = "{}";

    [Required]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[] RowVersion { get; set; } = null!;
}
