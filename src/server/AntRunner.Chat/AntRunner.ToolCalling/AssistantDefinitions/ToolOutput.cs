namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// A list of tools for which the outputs are being submitted.
/// </summary>
public class ToolOutput
{
    /// <summary>
    /// The ID of the tool call in the required_action object
    /// within the run object the output is being submitted for.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// The output of the tool call to be submitted to continue the run.
    /// </summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }
}

