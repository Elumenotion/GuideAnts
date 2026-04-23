namespace GuideAntsApi.Options;

/// <summary>
/// Limits how much extracted notebook-file markdown is inlined into LLM chat messages (after stripping inline base64 images).
/// </summary>
public class MarkdownAttachmentOptions
{
    public const string SectionName = "MarkdownAttachment";

    public const int DefaultMaxInlineCharacters = 100_000;

    /// <summary>
    /// Maximum character count of extracted markdown (after stripping inline image data URLs) to send inline to the model.
    /// If exceeded, only a short message referencing the attachment file path (Rule 1 in attachment handling) is sent.
    /// </summary>
    public int MaxInlineCharacters { get; set; } = DefaultMaxInlineCharacters;
}
