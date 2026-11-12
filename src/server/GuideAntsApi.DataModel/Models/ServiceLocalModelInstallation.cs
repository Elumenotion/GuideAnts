using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// API-owned install memory for auxiliary local models/bundles (TTS, ASR, Embeddings,
/// Image Generation) — the aux analogue of <see cref="LocalModelInstallation"/> (llama).
/// One row per (service, model ref): what the API downloaded, from which catalog
/// coordinates, into which directory. Written only by API-originated actions (Plane B).
///
/// <para>
/// Provenance is shallower than llama's by necessity: the aux engines never report
/// repository, resolved revision, or artifact digest over HTTP (plan F-5/O-2). Columns
/// that would require an engine contract change were deliberately dropped rather than
/// left permanently null.
/// </para>
///
/// <para>
/// D4 rail: NO code path may write a <c>ServiceModes</c> selection from this record or
/// from any inventory/ready read. This is memory of what the API did, never an authority
/// for what should run. Presence is always a Plane C observation, never inferred from
/// the existence of a row (limit L-1: install memory is not evidence of presence).
/// </para>
/// </summary>
public class ServiceLocalModelInstallation
{
    [Key]
    [StringLength(64)]
    public string ServiceId { get; set; } = string.Empty;

    [Key]
    [StringLength(512)]
    public string ModelRef { get; set; } = string.Empty;

    /// <summary>"model" | "bundle". Bundle id for ImageGeneration.</summary>
    [Required]
    [StringLength(32)]
    public string ContentKind { get; set; } = "model";

    /// <summary>The API's own download payload model_id (or bundle id for SD).</summary>
    [StringLength(256)]
    public string? CatalogId { get; set; }

    /// <summary>Read once from GET /admin/catalog at download acceptance.</summary>
    [StringLength(64)]
    public string? CatalogVersion { get; set; }

    /// <summary>The API's own payload revision, else null.</summary>
    [StringLength(128)]
    public string? RequestedRevision { get; set; }

    /// <summary>From the /admin/models listing after terminal success.</summary>
    [StringLength(512)]
    public string? TargetDirectory { get; set; }

    /// <summary>
    /// Engine operation id of the API-originated download that produced (or is producing)
    /// this row. Lets the operation-poll endpoint find the row it enriches without any
    /// engine-side state. Null for rows recorded before this column existed.
    /// </summary>
    [StringLength(128)]
    public string? LastOperationId { get; set; }

    /// <summary>JSON array of {path, sizeBytes} — no digest (engines never report one).</summary>
    [Required]
    public string ArtifactsJson { get; set; } = "[]";

    /// <summary>Accepted | Installed | Failed | ArtifactMissing. API-owned lifecycle.</summary>
    [Required]
    [StringLength(32)]
    public string State { get; set; } = "Accepted";

    /// <summary>
    /// Separate observation track written ONLY by Plane C (the probe loop / load ops).
    /// Never mutates State — observation and intent must not blur (D3/D4).
    /// </summary>
    [StringLength(32)]
    public string? ObservedState { get; set; }

    public DateTime? ObservedUtc { get; set; }

    [StringLength(512)]
    public string? ObservedError { get; set; }

    [Required]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
