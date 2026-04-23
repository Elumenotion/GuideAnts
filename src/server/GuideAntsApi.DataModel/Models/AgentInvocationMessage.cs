using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Represents a single message within an agent invocation thread.
/// Stores the complete message history for visualization and evaluation.
/// </summary>
[Index(nameof(AgentInvocationId), nameof(Sequence))]
public class AgentInvocationMessage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The agent invocation this message belongs to.
    /// </summary>
    [Required]
    public Guid AgentInvocationId { get; set; }
    public AgentInvocation AgentInvocation { get; set; } = null!;

    /// <summary>
    /// Sequential message number for ordering (0, 1, 2...).
    /// </summary>
    [Required]
    public int Sequence { get; set; }

    /// <summary>
    /// Message role: System, User, Assistant, Tool
    /// </summary>
    [Required]
    public ChatRole Role { get; set; }

    /// <summary>
    /// Message content.
    /// </summary>
    [Required]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// For Assistant messages with tool calls: JSON serialization of ToolCall list.
    /// </summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>
    /// For Tool messages: The function name that was invoked.
    /// </summary>
    [StringLength(255)]
    public string? FunctionName { get; set; }

    /// <summary>
    /// For Tool messages: The tool call ID this is a response to.
    /// </summary>
    [StringLength(100)]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// UTC timestamp when this message was created.
    /// </summary>
    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;
}



