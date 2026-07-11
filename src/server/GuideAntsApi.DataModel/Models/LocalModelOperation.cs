using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Authoritative durable operation record for local model lifecycle actions.
/// Immutable input is captured at creation; status and side effects are updated explicitly.
/// </summary>
public class LocalModelOperation
{
    [Key]
    public Guid OperationId { get; set; } = Guid.NewGuid();

    [Required]
    [StringLength(64)]
    public string OperationKind { get; set; } = string.Empty;

    [StringLength(128)]
    public string? ModelId { get; set; }

    [StringLength(128)]
    public string? RouterModelId { get; set; }

    /// <summary>
    /// Immutable operation input captured before work begins. Never updated after insert.
    /// </summary>
    [Required]
    public string ImmutableInputJson { get; set; } = "{}";

    [Required]
    [StringLength(64)]
    public string Status { get; set; } = "queued";

    [StringLength(64)]
    public string? CurrentStep { get; set; }

  /// <summary>
    /// JSON object recording completed side effects keyed by step name.
    /// </summary>
    [Required]
    public string CompletedSideEffectsJson { get; set; } = "{}";

    [StringLength(64)]
    public string? ErrorCode { get; set; }

    [StringLength(2048)]
    public string? ErrorMessage { get; set; }

    [StringLength(2048)]
    public string? Remediation { get; set; }

    public int? DesiredRevision { get; set; }

    public int? AppliedRevision { get; set; }

    [Required]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = null!;
}
