namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// An image produced by a tool call, presented to the model as a synthetic
/// user message with attachment (see <c>AntRunner.Chat.MessageComposer</c>).
/// The data URL is deliberately never part of any tool-result text.
/// </summary>
/// <param name="DataUrl">Base64 data URL of the image (e.g. <c>data:image/png;base64,...</c>).</param>
/// <param name="Caption">Short description of why the image was retrieved.</param>
/// <param name="SourcePath">Notebook-relative path the image was retrieved from.</param>
/// <param name="SourceTool">Operation id of the tool that produced the attachment.</param>
/// <param name="NotebookFileId">Id of the notebook file the image came from. Used to link the
/// persisted attachment message row so the image re-expands across turns; null when unavailable.</param>
public sealed record VisionAttachment(
    string DataUrl,
    string Caption,
    string SourcePath,
    string SourceTool,
    Guid? NotebookFileId = null);

/// <summary>
/// Return type for native tools that present images to the model.
/// <see cref="Output"/> is the model-facing tool-result text and never
/// contains image bytes; <see cref="Attachments"/> ride a transient
/// side channel on <see cref="ToolOutput"/> and are composed into the
/// synthetic bridge + user-attachment message sequence by the run loop.
/// </summary>
public sealed class VisionToolResult
{
    /// <summary>Model-facing tool result text. Must not contain image bytes.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Images to present to the model after the tool result message. Empty for failures.</summary>
    public IReadOnlyList<VisionAttachment> Attachments { get; init; } = [];
}
