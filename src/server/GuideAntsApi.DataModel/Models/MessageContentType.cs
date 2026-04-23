using System.Text.Json.Serialization;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Defines the type of content contained in a conversation message.
/// Used to determine how to render and process message content.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageContentType
{
    /// <summary>
    /// Plain text message content only.
    /// </summary>
    Text = 0,

    /// <summary>
    /// Message references a NotebookFile (AttachedNotebookFileId is populated).
    /// </summary>
    FileReference = 1,

    /// <summary>
    /// Message contains both text content and file references.
    /// </summary>
    Mixed = 2
} 