namespace GuideAntsApi.Services.Conversations.Tracing;

public sealed class TurnTraceEnvelope
{
    public int SchemaVersion { get; set; } = 1;

    public List<TurnTraceSegment> Segments { get; set; } = [];
}

public sealed class TurnTraceSegment
{
    public Guid SegmentId { get; set; } = Guid.NewGuid();

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// Segment status: partial, completed, cancelled, failed.
    /// </summary>
    public string Status { get; set; } = "partial";

    public string AssistantName { get; set; } = string.Empty;

    public string? ModelDeploymentId { get; set; }

    public List<TurnTraceMessage> SeedMessages { get; set; } = [];

    public List<TurnTraceToolDefinition> ToolDefinitions { get; set; } = [];

    public List<TurnTraceRound> Rounds { get; set; } = [];

    public List<TurnTraceMessageEvent> MessageEvents { get; set; } = [];

    public string? TerminalStatus { get; set; }

    public string? ErrorMessage { get; set; }

    public int? ToolLimitCallsUsed { get; set; }

    public int? ToolLimitRoundsUsed { get; set; }

    public string? ToolLimitEscalationPhase { get; set; }
}

public sealed class TurnTraceRound
{
    public int RoundIndex { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string? ModelDeploymentId { get; set; }

    public List<TurnTraceMessage> RequestMessages { get; set; } = [];

    public string? ResponseFinishReason { get; set; }

    public TurnTraceMessage? ResponseMessage { get; set; }

    public List<TurnTraceToolCall> ExternalToolCalls { get; set; } = [];
}

public sealed class TurnTraceMessage
{
    public string Role { get; set; } = string.Empty;

    public string? Content { get; set; }

    public string? ToolCallId { get; set; }

    public string? FunctionName { get; set; }

    public string? ToolCallsJson { get; set; }
}

public sealed class TurnTraceToolDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? ParametersJson { get; set; }

    /// <summary>
    /// Tool source: guide, client, or skills.
    /// </summary>
    public string Source { get; set; } = "guide";
}

public sealed class TurnTraceToolCall
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ArgumentsJson { get; set; }
}

public sealed class TurnTraceMessageEvent
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string Role { get; set; } = string.Empty;

    public string? Content { get; set; }

    public string? ToolCallId { get; set; }

    public string? FunctionName { get; set; }

    public string? ToolCallsJson { get; set; }
}
