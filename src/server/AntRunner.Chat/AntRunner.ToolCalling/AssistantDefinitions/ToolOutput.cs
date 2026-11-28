using System.Text.Json.Serialization;

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

    /// <summary>
    /// Images produced by the tool call, composed into the synthetic
    /// bridge + user-attachment message sequence by the run loop.
    /// Transient in-process side channel only: <c>[JsonIgnore]</c> because
    /// data URLs must never be persisted or sent to the client through this
    /// record. The image itself is persisted on the composed user message
    /// via the standard attachment path.
    /// </summary>
    [JsonIgnore]
    public List<VisionAttachment>? Attachments { get; set; }
}

