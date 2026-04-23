using System.Text.RegularExpressions;
using System.Web;

namespace GuideAntsApi.Utils;

/// <summary>
/// Utility for converting between absolute API URLs and relative paths in markdown content.
/// Enables portable markdown files that work across projects, notebooks, and local filesystems.
/// </summary>
public static class MarkdownUrlConverter
{
    /// <summary>
    /// Pattern to match notebook file content URLs in markdown.
    /// Matches: ![alt](http://domain/api/projects/{guid}/notebooks/{guid}/files/content?path=...)
    /// Also matches: [text](http://domain/api/projects/{guid}/notebooks/{guid}/files/content?path=...)
    /// Group 1: Link prefix (![alt] or [text])
    /// Group 2: Full URL
    /// Group 3: Path parameter value
    /// Group 4: Additional query params (e.g., m=12345)
    /// </summary>
    private static readonly Regex AbsoluteUrlPattern = new Regex(
        @"(!?\[[^\]]*\])\((https?://[^)]*?/(?:api/)?projects/[0-9a-f\-]+/notebooks/[0-9a-f\-]+/files/content\?path=([^)&]+)((?:&[^)]*)?)?)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Convert absolute API URLs to relative paths in markdown content.
    /// Used when storing markdown to make it portable.
    /// </summary>
    /// <param name="markdownContent">The markdown content to process</param>
    /// <returns>Markdown with relative paths</returns>
    public static string ConvertAbsoluteToRelative(string markdownContent)
    {
        if (string.IsNullOrEmpty(markdownContent))
            return markdownContent;

        return AbsoluteUrlPattern.Replace(markdownContent, match =>
        {
            var linkPart = match.Groups[1].Value;  // ![alt]( or [text](
            var pathParam = match.Groups[3].Value; // The path query parameter value
            var additionalParams = match.Groups[4].Value; // Additional query params like &m=12345

            try
            {
                // Decode the path parameter
                var decodedPath = HttpUtility.UrlDecode(pathParam);
                
                // Normalize path separators to forward slashes
                var normalizedPath = decodedPath.Replace('\\', '/');

                // Convert to relative path
                var relativePath = normalizedPath.StartsWith('/') 
                    ? $".{normalizedPath}"
                    : $"./{normalizedPath}";

                // Preserve the 'm' parameter (message timestamp) for cache busting if present
                // This allows clients to know when a file has been modified within a conversation
                if (!string.IsNullOrEmpty(additionalParams))
                {
                    // Parse the additional query params to find 'm='
                    foreach (var param in additionalParams.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (param.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
                        {
                            // Append the m parameter to the relative path
                            relativePath = $"{relativePath}?{param}";
                            break;
                        }
                    }
                }

                // Return the markdown with relative path
                return $"{linkPart}({relativePath})";
            }
            catch
            {
                // If anything fails, return the original match
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Convert relative paths to absolute authenticated API URLs in markdown content.
    /// Used when rendering markdown to enable authentication and authorization.
    /// 
    /// Note: This is primarily handled client-side. This server-side version is for
    /// scenarios where server needs to generate markdown with absolute URLs.
    /// </summary>
    /// <param name="markdownContent">The markdown content to process</param>
    /// <param name="projectId">Current project ID</param>
    /// <param name="notebookId">Current notebook ID</param>
    /// <param name="baseUrl">Base URL for API (e.g., https://api.server.com)</param>
    /// <returns>Markdown with absolute authenticated URLs</returns>
    public static string ConvertRelativeToAbsolute(
        string markdownContent,
        Guid projectId,
        Guid notebookId,
        string baseUrl)
    {
        if (string.IsNullOrEmpty(markdownContent))
            return markdownContent;

        // Pattern to match relative paths in markdown
        var relativePathPattern = new Regex(
            @"(!?\[[^\]]*\])\((?!https?://|/|mailto:|tel:|data:)(\.\.?/)?([^)]+)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        return relativePathPattern.Replace(markdownContent, match =>
        {
            var linkPart = match.Groups[1].Value;
            var relativePath = match.Groups[3].Value;

            try
            {
                // Skip fragments and special links
                if (relativePath.StartsWith("#") || relativePath.StartsWith("mailto:") || relativePath.StartsWith("tel:"))
                    return match.Value;

                // Skip if it contains ://  (already absolute)
                if (relativePath.Contains("://"))
                    return match.Value;

                // Build the absolute API URL
                var normalizedBase = baseUrl.TrimEnd('/');
                var encodedPath = HttpUtility.UrlEncode(relativePath);
                var absoluteUrl = $"{normalizedBase}/projects/{projectId}/notebooks/{notebookId}/files/content?path={encodedPath}";

                // Return the markdown with absolute URL
                return $"{linkPart}({absoluteUrl})";
            }
            catch
            {
                // If anything fails, return the original match
                return match.Value;
            }
        });
    }
}

