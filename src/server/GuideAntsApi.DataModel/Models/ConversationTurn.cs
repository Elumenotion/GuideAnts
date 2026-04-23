using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Represents a complete interaction cycle between user and assistant in a conversation.
/// A turn includes the user's instructions, the assistant's processing, and all resulting messages.
/// This entity tracks turn-level metadata, usage statistics, and file operations.
/// </summary>
[Index(nameof(NotebookConversationId), nameof(TurnIndex))]
public class ConversationTurn
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The conversation this turn belongs to.
    /// </summary>
    [Required]
    public Guid NotebookConversationId { get; set; }
    public NotebookConversation NotebookConversation { get; set; } = null!;

    /// <summary>
    /// Sequential turn number within the conversation (1, 2, 3...).
    /// Used to maintain proper turn ordering and enable turn-based operations.
    /// </summary>
    [Required]
    public int TurnIndex { get; set; }

    /// <summary>
    /// The name of the assistant that handled this turn.
    /// Single source of truth for current assistant (derived from last turn).
    /// </summary>
    [Required]
    [StringLength(255)]
    public string AssistantName { get; set; } = string.Empty;

    /// <summary>
    /// The model deployment ID used for this turn.
    /// Single source of truth for current model (derived from last turn).
    /// </summary>
    [StringLength(255)]
    public string? ModelDeploymentId { get; set; }

    /// <summary>
    /// The user's original instructions/prompt for this turn.
    /// </summary>
    [Required]
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialization of the complete ChatRunOutput for this turn.
    /// Includes usage statistics, status, dialog representation, and reasoning summaries.
    /// </summary>
    public string? ChatRunOutputJson { get; set; }

    /// <summary>
    /// JSON serialization of turn-specific usage statistics.
    /// Stored separately for efficient aggregation queries.
    /// </summary>
    public string? UsageJson { get; set; }

    /// <summary>
    /// JSON array of CWD-relative file paths that were created during this turn.
    /// Paths are relative to the working directory (Output/ for private notebooks, Runs/{runId}/ for published).
    /// Populated from ChatRunOutput.NewFiles during turn completion.
    /// </summary>
    public string? FilesCreated { get; set; }

    /// <summary>
    /// JSON array of CWD-relative file paths that were modified during this turn.
    /// Paths are relative to the working directory (Output/ for private notebooks, Runs/{runId}/ for published).
    /// Populated from ChatRunOutput.ModifiedFiles during turn completion.
    /// </summary>
    public string? FilesModified { get; set; }

    /// <summary>
    /// Directory context for file operations during this turn.
    /// Helps track where file operations occurred in the notebook filesystem.
    /// </summary>
    [StringLength(500)]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// UTC timestamp when this turn was created.
    /// </summary>
    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Status of the turn for collaboration tracking.
    /// Values: 'streaming', 'completed', 'cancelled'
    /// </summary>
    [Required]
    [StringLength(20)]
    public string Status { get; set; } = "completed";

    /// <summary>
    /// UTC timestamp when this turn was last updated.
    /// Used for efficient polling by observers to detect changes.
    /// </summary>
    [Required]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to messages that belong to this turn.
    /// </summary>
    public ICollection<NotebookConversationMessage> Messages { get; set; } = [];
} 