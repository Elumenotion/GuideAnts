using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Sidecar diagnostics trace for a conversation turn.
/// Captures prompt construction and tool-definition snapshots for drill-down diagnostics.
/// </summary>
[Index(nameof(ConversationTurnId), IsUnique = true)]
[Index(nameof(NotebookConversationId), nameof(TurnIndex))]
public class ConversationTurnTrace
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ConversationTurnId { get; set; }
    public ConversationTurn ConversationTurn { get; set; } = null!;

    [Required]
    public Guid NotebookConversationId { get; set; }

    [Required]
    public int TurnIndex { get; set; }

    [Required]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Aggregate state of persisted segments.
    /// Values: partial, completed, cancelled, failed.
    /// </summary>
    [Required]
    [StringLength(20)]
    public string CaptureState { get; set; } = "partial";

    /// <summary>
    /// Serialized trace payload (schemaVersion + segments).
    /// </summary>
    [Required]
    public string TraceJson { get; set; } = "{\"schemaVersion\":1,\"segments\":[]}";

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}
