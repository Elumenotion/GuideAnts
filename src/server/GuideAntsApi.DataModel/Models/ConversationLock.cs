using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Represents a distributed lock on a conversation for multi-container collaboration.
/// Ensures only one user can stream to a conversation at a time across all server instances.
/// Locks automatically expire after 5 minutes to handle crashes/disconnects.
/// </summary>
[Index(nameof(ExpiresAt))]
public class ConversationLock
{
    /// <summary>
    /// The conversation that is locked (Primary Key).
    /// </summary>
    [Key]
    public Guid ConversationId { get; set; }
    public NotebookConversation NotebookConversation { get; set; } = null!;

    /// <summary>
    /// Display name of the user holding the lock (denormalized for performance).
    /// Used for both display in UI and for comparison to determine if current user owns the lock.
    /// </summary>
    [Required]
    [StringLength(255)]
    public string LockedByUserName { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the lock was acquired.
    /// </summary>
    [Required]
    public DateTime LockedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when the lock will automatically expire.
    /// Default is 5 minutes after acquisition to handle crashes/disconnects.
    /// </summary>
    [Required]
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(5);
}

