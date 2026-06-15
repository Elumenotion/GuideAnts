using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Rewrites published guide file URLs in assistant markdown so MCP clients receive
/// links reachable from the caller's perspective (the incoming HTTP request origin).
/// </summary>
internal static class McpPublishedContentUrlRewriter
{
    private const string PublishedApiPrefix = "/api/published/";

    // Markdown images/links: ![alt](url) or [text](url)
    private static readonly Regex MarkdownUrlPattern = new(
        @"(?<head>(!\[[^\]]*\]|\[[^\]]*\])\()(?<url>[^)]+)(?<tail>\))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // HTML href="..." or src="..." (double-quoted)
    private static readonly Regex HtmlDoubleQuotedUrlPattern = new(
        @"(?<attr>href|src)\s*=\s*""(?<url>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // HTML href='...' or src='...' (single-quoted)
    private static readonly Regex HtmlSingleQuotedUrlPattern = new(
        @"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Rewrite(string? content, string? publicApiOrigin)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(publicApiOrigin))
        {
            return content ?? string.Empty;
        }

        var origin = publicApiOrigin.TrimEnd('/');

        var result = MarkdownUrlPattern.Replace(content, m =>
        {
            var rewritten = RewriteUrl(m.Groups["url"].Value, origin);
            return m.Groups["head"].Value + rewritten + m.Groups["tail"].Value;
        });

        result = HtmlDoubleQuotedUrlPattern.Replace(result, m =>
        {
            var rewritten = RewriteUrl(m.Groups["url"].Value, origin);
            return $"{m.Groups["attr"].Value}=\"{rewritten}\"";
        });

        result = HtmlSingleQuotedUrlPattern.Replace(result, m =>
        {
            var rewritten = RewriteUrl(m.Groups["url"].Value, origin);
            return $"{m.Groups["attr"].Value}='{rewritten}'";
        });

        return result;
    }

    private static string RewriteUrl(string url, string origin)
    {
        if (string.IsNullOrWhiteSpace(url) || !ContainsPublishedApiPath(url))
        {
            return url;
        }

        var publishedPathAndQuery = ExtractPublishedPathAndQuery(url);
        if (publishedPathAndQuery == null)
        {
            return url;
        }

        return origin + publishedPathAndQuery;
    }

    private static bool ContainsPublishedApiPath(string url) =>
        url.Contains(PublishedApiPrefix, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractPublishedPathAndQuery(string url)
    {
        if (url.StartsWith(PublishedApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return null;
        }

        var pathAndQuery = absolute.AbsolutePath;
        if (!string.IsNullOrEmpty(absolute.Query))
        {
            pathAndQuery += absolute.Query;
        }

        if (!pathAndQuery.StartsWith(PublishedApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return pathAndQuery;
    }
}
