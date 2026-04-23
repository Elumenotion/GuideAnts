using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.Options;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Prepares extracted markdown for LLM attachment: strips inline image data URLs, then enforces <see cref="MarkdownAttachmentOptions.MaxInlineCharacters"/>.
/// </summary>
public static class AttachmentMarkdownChatPolicy
{
    /// <summary>
    /// Builds the user-visible text for one attachment's extracted markdown block.
    /// </summary>
    public static string BuildMarkdownAttachmentUserText(
        string rawMarkdown,
        string fileName,
        string attachmentPath,
        int maxInlineCharacters,
        ILogger? logger,
        Guid? notebookFileId = null)
    {
        var (sanitized, replacementCount) = MarkdownInlineImageSanitizer.ReplaceInlineImageDataUrls(rawMarkdown);
        var originalLength = rawMarkdown.Length;

        if (replacementCount > 0)
        {
            logger?.LogInformation(
                "Attachment markdown inline images stripped for {FileName} (NotebookFileId: {NotebookFileId}): {ReplacementCount} replacement(s), chars {OriginalLength} -> {SanitizedLength}",
                fileName, notebookFileId, replacementCount, originalLength, sanitized.Length);
        }

        if (sanitized.Length > maxInlineCharacters)
        {
            logger?.LogInformation(
                "Attachment markdown too large to inline for {FileName} (NotebookFileId: {NotebookFileId}): {SanitizedLength} chars after stripping (max {MaxInlineCharacters}); using path reference only",
                fileName, notebookFileId, sanitized.Length, maxInlineCharacters);

            return
                $"Extracted markdown for {fileName} is too large to include inline ({sanitized.Length} characters after omitting inline images; limit {maxInlineCharacters}). " +
                $"Use the file at `{attachmentPath}` (see the Attachment line above).";
        }

        return $"Markdown content of {fileName}:\n\n{sanitized}";
    }
}
