namespace GuideAntsApi.DataModel.Models;

using System.ComponentModel.DataAnnotations;

public class MessageEditHistory
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid MessageId { get; set; }
    public NotebookConversationMessage Message { get; set; } = null!;

    [Required]
    public string OriginalContent { get; set; } = string.Empty;

    public string? OriginalToolCalls { get; set; }

    public Guid? FirstEditedByUserId { get; set; }
    public User? FirstEditedByUser { get; set; }

    [Required]
    public DateTime FirstEditedAt { get; set; } = DateTime.UtcNow;
} 