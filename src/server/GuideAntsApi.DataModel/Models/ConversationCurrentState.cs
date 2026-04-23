using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Keyless entity representing the current state of a conversation derived from the last turn.
/// This provides efficient access to current conversation state without data duplication.
/// Mapped to a database view using ToView() in ApplicationDbContext.
/// </summary>
[Keyless]
public class ConversationCurrentState
{
    /// <summary>
    /// Conversation ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Notebook that contains this conversation
    /// </summary>
    public Guid NotebookId { get; set; }

    /// <summary>
    /// Conversation title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Conversation summary
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// When the conversation was created
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Current assistant name (from last turn)
    /// </summary>
    public string? CurrentAssistantName { get; set; }

    /// <summary>
    /// Effective model deployment ID: conversation override if set, otherwise the last turn.
    /// </summary>
    public string? CurrentModelDeploymentId { get; set; }

    /// <summary>
    /// Last instructions given (from last turn)
    /// </summary>
    public string? LastInstructions { get; set; }

    /// <summary>
    /// Last activity timestamp (from last turn)
    /// </summary>
    public DateTime? LastActivity { get; set; }

    /// <summary>
    /// Current turn index (from last turn)
    /// </summary>
    public int? CurrentTurnIndex { get; set; }

    /// <summary>
    /// Files created in the last turn (JSON array of NotebookFile IDs)
    /// </summary>
    public string? LastTurnFilesCreated { get; set; }

    /// <summary>
    /// Files modified in the last turn (JSON array of NotebookFile IDs)
    /// </summary>
    public string? LastTurnFilesModified { get; set; }
} 