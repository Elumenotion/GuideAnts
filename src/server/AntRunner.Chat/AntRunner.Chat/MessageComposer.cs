using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.AssistantDefinitions;

namespace AntRunner.Chat;

/// <summary>
/// Composes the calibrated message sequence that presents tool-retrieved images
/// to the model without relying on any provider's tool-result multimodal support.
///
/// For a tool batch that produced images the run loop appends, after all of the
/// batch's normal tool-result messages, a synthetic <c>assistant</c> bridge
/// followed by a single synthetic <c>user</c> message carrying every image in the
/// batch, each with its provenance caption.
///
/// The sequence is provider-independent by construction: images ride a user-role
/// message (accepted by every routed provider), and the assistant bridge keeps
/// role alternation valid for strict providers (e.g. Gemini turn rules). The
/// assistant bridge is unconditional. Tool-result messages themselves remain
/// normal (non-synthetic) so tool_call_id replay and dedup are unaffected.
/// </summary>
public static class MessageComposer
{
    /// <summary>
    /// Composes the [assistant bridge, user attachment] pair for a tool batch that
    /// produced one or more image attachments. Returns two synthetic messages.
    /// Throws if <paramref name="attachments"/> is empty (callers gate on count).
    /// </summary>
    public static (ChatMessage Bridge, ChatMessage Attachment) ComposeAttachmentSequence(
        IReadOnlyList<VisionAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            throw new ArgumentException("ComposeAttachmentSequence requires at least one attachment.", nameof(attachments));
        }

        var bridgeText = attachments.Count == 1
            ? $"Presenting the retrieved image as the output of {attachments[0].SourceTool} for inspection. It follows in the attached message."
            : $"Presenting the {attachments.Count} retrieved images as the output of {string.Join(", ", attachments.Select(a => a.SourceTool).Distinct())} for inspection. They follow in the attached message.";

        var bridge = ChatMessage.Synthetic(ChatRole.Assistant, [new ChatContent(bridgeText)]);

        var userParts = new List<ChatContent>(attachments.Count + 1);
        userParts.Add(new ChatContent(BuildAttachmentCaption(attachments)));
        foreach (var attachment in attachments)
        {
            userParts.Add(new ChatContent(new ChatImageUrl(attachment.DataUrl)));
        }
        var attachmentMessage = ChatMessage.Synthetic(ChatRole.User, userParts);

        return (bridge, attachmentMessage);
    }

    /// <summary>
    /// Builds the provenance caption for the synthetic user message. It explicitly
    /// frames the images as tool output, not new user input, so the model does not
    /// mistake the attachment for a fresh user request (requirement R5).
    /// </summary>
    public static string BuildAttachmentCaption(IReadOnlyList<VisionAttachment> attachments)
    {
        var lines = new List<string>
        {
            "[The following image(s) were produced by the tool call(s) immediately above. They are tool output presented for inspection, not new user input.]"
        };
        foreach (var attachment in attachments)
        {
            lines.Add($"- {attachment.SourcePath} (retrieved by {attachment.SourceTool}): {attachment.Caption}");
        }
        return string.Join("\n", lines);
    }
}
