using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Junction table linking messages to their attached files.
/// Supports multiple attachments per message and tracks attachment metadata.
/// </summary>
public class MessageAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The message this attachment belongs to.
    /// </summary>
    [Required]
    public Guid MessageId { get; set; }
    public NotebookConversationMessage Message { get; set; } = null!;

    /// <summary>
    /// The notebook file being attached.
    /// </summary>
    [Required]
    public Guid NotebookFileId { get; set; }
    public NotebookFile NotebookFile { get; set; } = null!;

    /// <summary>
    /// The type of attachment relationship.
    /// </summary>
    [Required]
    public AttachmentType Type { get; set; } = AttachmentType.Referenced;

    /// <summary>
    /// Order of this attachment within the message (0-based).
    /// </summary>
    [Required]
    public int OrderIndex { get; set; }

    /// <summary>
    /// When this attachment was added to the message.
    /// </summary>
    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Defines the type of attachment relationship.
/// </summary>
public enum AttachmentType
{
    /// <summary>
    /// File was referenced/attached by user in their message.
    /// </summary>
    Referenced = 0,

    /// <summary>
    /// File was created by the assistant during this conversation turn.
    /// </summary>
    Created = 1,

    /// <summary>
    /// File was modified by the assistant during this conversation turn.
    /// </summary>
    Modified = 2
} 