using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

[Index(nameof(NotebookId))]
[Index(nameof(NotebookId), nameof(Created))]
public class NotebookConversation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid NotebookId { get; set; }
    public Notebook Notebook { get; set; } = null!;

    [Required]
    [StringLength(255)]
    public string Title { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public ICollection<NotebookConversationMessage> Messages { get; set; } = [];

    /// <summary>
    /// Navigation property to the turns in this conversation.
    /// Turns represent complete interaction cycles between user and assistant.
    /// </summary>
    public ICollection<ConversationTurn> Turns { get; set; } = [];

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;
} 