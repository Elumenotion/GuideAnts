using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

// Index for home page query: "conversations where current user participated"
// Supports efficient EXISTS check on Messages.Any(m => m.UserId == currentUser.Id)
[Index(nameof(UserId), nameof(NotebookConversationId))]
public class NotebookConversationMessage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid NotebookConversationId { get; set; }
    public NotebookConversation NotebookConversation { get; set; } = null!;
    
    // The user who created this message. Null for assistant/tool messages.
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// External user identity for published guide users authenticated via webhook.
    /// Used when UserId is null. Stores the userIdentity returned by the auth webhook.
    /// Examples: email, username, external ID, etc. - format determined by implementor.
    /// </summary>
    [StringLength(255)]
    public string? ExternalUserIdentity { get; set; }

    [Required]
    public ChatRole Role { get; set; }

    // NEW: Assistant FK for reliable attribution (null for System/User messages)
    public Guid? AssistantId { get; set; }
    public Assistant? Assistant { get; set; }

    [Required]
    public string Content { get; set; } = string.Empty;

    // For Assistant messages: The assistant that generated the response
    [StringLength(255)]
    public string? AssistantName { get; set; }

    // For Assistant messages: The model used
    [StringLength(255)]
    public string? ModelDeploymentId { get; set; }

    // For Assistant messages: JSON serialization of Anthropic thinking blocks
    public string? ThinkingBlocksJson { get; set; }

    // For Assistant messages: JSON serialization of ToolCall list
    public string? ToolCalls { get; set; }

    // For Tool messages: The function name invoked
    public string? FunctionName { get; set; }

    // For Tool messages: The ID of the tool call this is a response to
    public string? ToolCallId { get; set; }

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    // --- Turn and File Integration ---
    
    /// <summary>
    /// The turn index this message belongs to within the conversation.
    /// Links messages to their corresponding ConversationTurn for proper organization.
    /// </summary>
    [Required]
    public int TurnIndex { get; set; }

    /// <summary>
    /// Sequential message number within the turn for proper ordering.
    /// Ensures messages appear in correct chronological order within each turn.
    /// </summary>
    [Required]
    public int MessageSequence { get; set; }

    /// <summary>
    /// Collection of files attached to this message.
    /// Supports multiple attachments per message via MessageAttachment junction table.
    /// </summary>
    public ICollection<MessageAttachment> Attachments { get; set; } = [];

    /// <summary>
    /// Indicates the type of content in this message (Text, FileReference, Mixed).
    /// </summary>
    [Required]
    public MessageContentType MessageContentType { get; set; } = MessageContentType.Text;

    /// <summary>
    /// Navigation property to the turn this message belongs to.
    /// </summary>
    public ConversationTurn? ConversationTurn { get; set; }

    // --- Edit Tracking ---
    public bool IsEdited { get; set; } = false;

    public Guid? LastEditedByUserId { get; set; }
    public User? LastEditedByUser { get; set; }

    public DateTime? LastEditedAt { get; set; }

    public MessageEditHistory? EditHistory { get; set; }

    /// <summary>
    /// Indicates whether the assistant message is still streaming (true) or completed (false).
    /// Null for legacy rows created before streaming support.
    /// </summary>
    public bool? IsStreaming { get; set; }
} 