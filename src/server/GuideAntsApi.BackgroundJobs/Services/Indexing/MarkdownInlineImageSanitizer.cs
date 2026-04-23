using System.Text.RegularExpressions;

namespace GuideAntsApi.BackgroundJobs.Services.Indexing;

/// <summary>
/// Replaces inline <c>data:image/...;base64,...</c> payloads in markdown/HTML-like text with a short placeholder.
/// Used by <see cref="HybridIndexer"/> (embeddings) and by the API when attaching extracted markdown to chat.
/// </summary>
public static class MarkdownInlineImageSanitizer
{
    public const string InlineImagePlaceholder = "[inline-image omitted]";

    public static readonly Regex InlineImageDataUrlRegex = new(
        @"data:image\/[a-z0-9.+-]+;base64,[A-Za-z0-9+\/=%\\\s_-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces each inline image data URL with <see cref="InlineImagePlaceholder"/>.
    /// </summary>
    /// <returns>Sanitized text and the number of replacements performed.</returns>
    public static (string SanitizedText, int ReplacementCount) ReplaceInlineImageDataUrls(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text ?? string.Empty, 0);
        }

        var replacements = 0;
        var sanitized = InlineImageDataUrlRegex.Replace(text, _ =>
        {
            replacements++;
            return InlineImagePlaceholder;
        });
        return (sanitized, replacements);
    }
}
