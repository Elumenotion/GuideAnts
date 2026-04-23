using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Represents a single agent invocation thread spawned during a conversation turn.
/// Agent invocations are non-interactive, one-shot executions that occur when a parent
/// assistant calls the InvokeAgent tool. These threads are stored separately from the
/// main conversation to enable visualization and evaluation of multi-agent workflows.
/// </summary>
[Index(nameof(ParentConversationId), nameof(ParentTurnIndex))]
[Index(nameof(ParentInvocationId))]
[Index(nameof(Created))]
public class AgentInvocation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    // --- Parent Context ---

    /// <summary>
    /// The root notebook conversation this invocation belongs to.
    /// All invocations in a multi-agent chain share the same parent conversation.
    /// </summary>
    [Required]
    public Guid ParentConversationId { get; set; }
    public NotebookConversation ParentConversation { get; set; } = null!;

    /// <summary>
    /// The turn index within the parent conversation where this invocation was triggered.
    /// </summary>
    [Required]
    public int ParentTurnIndex { get; set; }

    /// <summary>
    /// The specific tool call ID that triggered this invocation.
    /// Useful for correlating invocation results with tool call messages.
    /// </summary>
    [StringLength(100)]
    public string? TriggeringToolCallId { get; set; }

    /// <summary>
    /// Parent agent invocation for nested invocations (agent invoking agent).
    /// Null for top-level invocations triggered directly from the main conversation.
    /// </summary>
    public Guid? ParentInvocationId { get; set; }
    public AgentInvocation? ParentInvocation { get; set; }

    /// <summary>
    /// Child invocations spawned by this invocation.
    /// </summary>
    public ICollection<AgentInvocation> ChildInvocations { get; set; } = [];

    // --- Invocation Details ---

    /// <summary>
    /// The assistant/guide record that this invocation executed.
    /// </summary>
    [Required]
    public Guid AssistantId { get; set; }
    public Assistant Assistant { get; set; } = null!;

    /// <summary>
    /// The name of the assistant that was invoked.
    /// </summary>
    [Required]
    [StringLength(255)]
    public string AssistantName { get; set; } = string.Empty;

    /// <summary>
    /// The model deployment ID used for this invocation.
    /// </summary>
    [StringLength(255)]
    public string? ModelDeploymentId { get; set; }

    /// <summary>
    /// The instructions/prompt passed to the invoked agent.
    /// </summary>
    [Required]
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// The context options message injected before assistant instructions.
    /// JSON format: {"contextOptions": {...}}
    /// </summary>
    public string? ContextMessageJson { get; set; }

    /// <summary>
    /// The evaluator assistant name if one was configured for this invocation.
    /// </summary>
    [StringLength(255)]
    public string? Evaluator { get; set; }

    // --- Status ---

    /// <summary>
    /// Status of the invocation: 'running', 'completed', 'failed', 'cancelled'
    /// </summary>
    [Required]
    [StringLength(20)]
    public string Status { get; set; } = "running";

    /// <summary>
    /// Error message if the invocation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    // --- Metrics ---

    /// <summary>
    /// JSON serialization of token usage statistics.
    /// Format: { "promptTokens": N, "completionTokens": N, "cachedPromptTokens": N, "totalTokens": N }
    /// </summary>
    public string? UsageJson { get; set; }

    /// <summary>
    /// Number of LLM round-trips in this invocation (including tool call loops).
    /// </summary>
    public int LlmRoundTrips { get; set; } = 0;

    /// <summary>
    /// Number of tool calls executed during this invocation.
    /// </summary>
    public int ToolCallCount { get; set; } = 0;

    /// <summary>
    /// Depth level in the invocation hierarchy (0 = top-level from conversation).
    /// </summary>
    [Required]
    public int Depth { get; set; } = 0;

    // --- Timestamps ---

    /// <summary>
    /// UTC timestamp when this invocation started.
    /// </summary>
    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when this invocation completed.
    /// </summary>
    public DateTime? Completed { get; set; }

    /// <summary>
    /// Duration in milliseconds from start to completion.
    /// </summary>
    public long? DurationMs { get; set; }

    // --- Navigation ---

    /// <summary>
    /// Messages exchanged during this invocation.
    /// </summary>
    public ICollection<AgentInvocationMessage> Messages { get; set; } = [];
}


