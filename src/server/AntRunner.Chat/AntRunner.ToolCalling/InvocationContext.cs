using System.Linq;
using GuideAntsApi.DataModel;

namespace AntRunner.ToolCalling;

public sealed record InvocationContext(
    Guid ProjectId,
    Guid NotebookId,
    Guid ConversationId,
    string? OAuthUserAccessToken = null)
{
    /// <summary>
    /// Unique 10-character nano-id assigned for each published-notebook invocation.
    /// For private notebooks it may be null.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    /// The current turn index in the parent conversation.
    /// Set when invocations are triggered from ConversationService.
    /// </summary>
    public int TurnIndex { get; set; }

    /// <summary>
    /// The AgentInvocation ID if running within an agent invocation context.
    /// Null for top-level conversation runs. Set for Agent.Invoke calls.
    /// </summary>
    public Guid? CurrentInvocationId { get; set; }

    /// <summary>
    /// The notebook conversation message used to attribute top-level tool/service usage.
    /// Null for usage recorded inside agent invocations.
    /// </summary>
    public Guid? NotebookConversationMessageId { get; set; }

    /// <summary>
    /// Returns the conversation message attribution only when this context is not
    /// already attributed to an agent invocation.
    /// </summary>
    public Guid? NotebookConversationMessageIdForUsage =>
        CurrentInvocationId.HasValue ? null : NotebookConversationMessageId;

    /// <summary>
    /// Depth in the invocation hierarchy. 0 = called from main conversation.
    /// </summary>
    public int InvocationDepth { get; set; } = 0;

    /// <summary>
    /// The tool call ID that triggered this invocation (for correlation).
    /// </summary>
    public string? TriggeringToolCallId { get; set; }

    /// <summary>
    /// The assistant/guide that is executing this context.
    /// Used for usage attribution.
    /// </summary>
    public Guid? AssistantId { get; set; }

    public bool IsPublished { get; init; } = CheckIsPublished(NotebookId);

    private static bool CheckIsPublished(Guid notebookId)
    {
        try
        {
            using var context = ApplicationDbContext.Factory();
            return context.PublishedGuides
                .Any(pg => pg.NotebookId == notebookId && pg.Active);
        }
        catch
        {
            return false;
        }
    }
}

