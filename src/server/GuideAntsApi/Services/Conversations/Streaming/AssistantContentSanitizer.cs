using System.Text.Json;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed record ConversationFileUrlContext(
    Guid ProjectId,
    Guid NotebookId,
    Guid ConversationId,
    string? PublisherId,
    string? HostUrl);

/// <summary>
/// Shared generated-content URL normalization for private and published conversation flows.
/// </summary>
public static class AssistantContentSanitizer
{
    public static string AppendQueryParamIfMissing(string url, string key, string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (url.Contains(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            var separator = url.Contains('?') ? "&" : "?";
            return url + separator + key + "=" + Uri.EscapeDataString(value);
        }
        catch
        {
            var separator = url.Contains('?') ? "&" : "?";
            return url + separator + key + "=" + Uri.EscapeDataString(value);
        }
    }

    public static string NormalizeSandboxPath(string sandboxPath, bool includeRelativePrefix)
    {
        var path = sandboxPath.TrimStart('/');

        if (path.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(4);
        }

        var contentFilesPattern = new Regex(
            @"^ContentFiles/([^/]+/notebooks/[^/]+|[^/]+/[^/]+)/",
            RegexOptions.IgnoreCase);
        path = contentFilesPattern.Replace(path, string.Empty);

        if (string.IsNullOrEmpty(path))
        {
            return includeRelativePrefix ? "./" : string.Empty;
        }

        return includeRelativePrefix ? "./" + path : path;
    }

    public static string ConvertSandboxUrlsToRelative(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("sandbox:", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var pattern = new Regex(
            @"sandbox:/(?<path>[^\])""'\s<>]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        return pattern.Replace(content, m =>
            NormalizeSandboxPath(m.Groups["path"].Value, includeRelativePrefix: true));
    }

    public static string ConvertSandboxUrlsToPublished(string content, ConversationFileUrlContext ctx)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("sandbox:", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var mdPattern = new Regex(
            @"(?<head>!?\[[^\]]*\]\()sandbox:/(?<path>[^)]+)(?<tail>\))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var result = mdPattern.Replace(content, m =>
        {
            var relativePath = NormalizeSandboxPath(m.Groups["path"].Value, includeRelativePrefix: false);
            var publishedUrl = BuildPublishedFileUrl(ctx, relativePath);
            return m.Groups["head"].Value + publishedUrl + m.Groups["tail"].Value;
        });

        var htmlDqPattern = new Regex(
            @"(?<attr>href|src)\s*=\s*""sandbox:/(?<path>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        result = htmlDqPattern.Replace(result, m =>
        {
            var relativePath = NormalizeSandboxPath(m.Groups["path"].Value, includeRelativePrefix: false);
            var publishedUrl = BuildPublishedFileUrl(ctx, relativePath);
            return $"{m.Groups["attr"].Value}=\"{publishedUrl}\"";
        });

        var htmlSqPattern = new Regex(
            @"(?<attr>href|src)\s*=\s*'sandbox:/(?<path>[^']+)'",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        result = htmlSqPattern.Replace(result, m =>
        {
            var relativePath = NormalizeSandboxPath(m.Groups["path"].Value, includeRelativePrefix: false);
            var publishedUrl = BuildPublishedFileUrl(ctx, relativePath);
            return $"{m.Groups["attr"].Value}='{publishedUrl}'";
        });

        var cleanupPattern = new Regex(
            @"sandbox:/(?<path>[^\])""'\s<>]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        result = cleanupPattern.Replace(result, m =>
            NormalizeSandboxPath(m.Groups["path"].Value, includeRelativePrefix: true));

        return result;
    }

    public static string BuildPublishedFileUrl(ConversationFileUrlContext ctx, string relativePath)
    {
        var pathEncoded = Uri.EscapeDataString(relativePath);
        var basePath =
            $"/api/published/projects/{ctx.ProjectId}/notebooks/{ctx.NotebookId}/conversations/{ctx.ConversationId}/files/content?path={pathEncoded}";
        if (!string.IsNullOrWhiteSpace(ctx.PublisherId))
        {
            basePath += $"&pubId={Uri.EscapeDataString(ctx.PublisherId)}";
        }

        return basePath;
    }

    public static Dictionary<string, string> ExtractPrivateFilenameUrlMapFromToolMessage(
        string toolMessageContent,
        ConversationFileUrlContext ctx)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(toolMessageContent))
        {
            return map;
        }

        var textToScan = toolMessageContent;
        try
        {
            using var doc = JsonDocument.Parse(toolMessageContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("StandardOutput", out var so)
                && so.ValueKind == JsonValueKind.String)
            {
                textToScan = so.GetString() ?? toolMessageContent;
            }
        }
        catch
        {
            // scan raw string
        }

        var lines = textToScan.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var normalizedHost = string.IsNullOrWhiteSpace(ctx.HostUrl)
            ? null
            : new Uri(ctx.HostUrl).GetLeftPart(UriPartial.Authority).TrimEnd('/');

        for (var i = 0; i < lines.Length; i++)
        {
            var header = lines[i].Trim();
            var isNew = header.Equals("New Files", StringComparison.OrdinalIgnoreCase);
            var isModified = header.Equals("Modified Files", StringComparison.OrdinalIgnoreCase);
            if (!isNew && !isModified)
            {
                continue;
            }

            var j = i + 1;
            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
            {
                j++;
            }

            if (j < lines.Length && lines[j].Trim().Equals("---", StringComparison.Ordinal))
            {
                j++;
            }

            for (; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(normalizedHost)
                    && line.StartsWith(normalizedHost, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
                    {
                        continue;
                    }

                    string? filename = null;
                    var query = uri.Query.TrimStart('?');
                    foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var idx = pair.IndexOf('=');
                        if (idx <= 0)
                        {
                            continue;
                        }

                        var key = pair.Substring(0, idx);
                        if (!key.Equals("path", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var value = Uri.UnescapeDataString(pair.Substring(idx + 1));
                        filename = Path.GetFileName(value);
                        break;
                    }

                    filename ??= Path.GetFileName(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(filename))
                    {
                        map[filename] = line;
                    }
                }
                else if (line.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = line.Substring("File:".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(rel) && !string.IsNullOrWhiteSpace(ctx.HostUrl))
                    {
                        var uriBuilder = new UriBuilder(ctx.HostUrl);
                        uriBuilder.Path = $"api/projects/{ctx.ProjectId}/notebooks/{ctx.NotebookId}/files/content";
                        uriBuilder.Query = $"path={Uri.EscapeDataString(rel)}";
                        var built = uriBuilder.Uri.ToString();
                        var filename = Path.GetFileName(rel);
                        if (!string.IsNullOrWhiteSpace(filename))
                        {
                            map[filename] = built;
                        }
                    }
                }
            }
        }

        return map;
    }

    public static Dictionary<string, string> ExtractPublishedFilenamePathMapFromToolMessage(string toolMessageContent)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(toolMessageContent))
        {
            return map;
        }

        var textToScan = toolMessageContent;
        try
        {
            using var doc = JsonDocument.Parse(toolMessageContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("StandardOutput", out var so) && so.ValueKind == JsonValueKind.String)
                {
                    textToScan = so.GetString() ?? toolMessageContent;
                }

                ExtractFilenamePathsFromJsonArray(doc.RootElement, "NewFiles", map);
                ExtractFilenamePathsFromJsonArray(doc.RootElement, "ModifiedFiles", map);
            }
        }
        catch
        {
            // continue with raw scan
        }

        var lines = textToScan.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var header = lines[i].Trim();
            if (!header.Equals("New Files", StringComparison.OrdinalIgnoreCase)
                && !header.Equals("Modified Files", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var j = i + 1;
            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
            {
                j++;
            }

            if (j < lines.Length && lines[j].Trim().Equals("---", StringComparison.Ordinal))
            {
                j++;
            }

            for (; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                if (line.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = line.Substring("File:".Length).Trim().Replace("\\", "/");
                    var filename = Path.GetFileName(rel);
                    if (!string.IsNullOrWhiteSpace(filename) && !map.ContainsKey(filename))
                    {
                        map[filename] = rel;
                    }
                }
            }
        }

        return map;
    }

    public static string SanitizePrivateAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        string? hostUrl)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var result = ConvertSandboxUrlsToRelative(content);
        if (filenameUrlMap.Count == 0)
        {
            return Utils.MarkdownUrlConverter.ConvertAbsoluteToRelative(result);
        }

        var messageStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        result = ApplyFilenameUrlReplacements(result, filenameUrlMap, messageStamp);
        return Utils.MarkdownUrlConverter.ConvertAbsoluteToRelative(result);
    }

    public static string SanitizePublishedAssistantContent(
        string content,
        IDictionary<string, string> filenameToPublishedUrl,
        ConversationFileUrlContext ctx)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var result = ConvertSandboxUrlsToPublished(content, ctx);
        if (filenameToPublishedUrl.Count == 0)
        {
            return ConvertNotebookFileUrlsToPublished(result, ctx);
        }

        var messageStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        result = ApplyFilenameUrlReplacements(result, filenameToPublishedUrl, messageStamp);
        return ConvertNotebookFileUrlsToPublished(result, ctx);
    }

    public static string ConvertNotebookFileUrlsToPublished(string markdownContent, ConversationFileUrlContext ctx)
    {
        if (string.IsNullOrEmpty(markdownContent))
        {
            return markdownContent;
        }

        var mdPattern = new Regex(
            @"(?<head>(!\[[^\]]*\]|\[[^\]]*\])\()(?<url>[^)]+)(?<tail>\))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var htmlDqPattern = new Regex(
            @"(?<attr>href|src)\s*=\s*""(?<url>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var htmlSqPattern = new Regex(
            @"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        string Rewriter(string originalUrl)
        {
            try
            {
                var u = new Uri(originalUrl, UriKind.RelativeOrAbsolute);
                var path = u.IsAbsoluteUri ? u.AbsolutePath : originalUrl;
                var query = u.IsAbsoluteUri
                    ? u.Query
                    : (originalUrl.Contains('?')
                        ? originalUrl.Substring(originalUrl.IndexOf('?', StringComparison.Ordinal))
                        : string.Empty);

                if (!path.StartsWith('/'))
                {
                    return originalUrl;
                }

                var apiPrefix = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ? "/api" : string.Empty;
                var expectedPrefix = $"{apiPrefix}/projects/{ctx.ProjectId}/notebooks/{ctx.NotebookId}/files/content";
                if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return originalUrl;
                }

                string? relativeFilePath = null;
                string? mValue = null;
                if (!string.IsNullOrEmpty(query))
                {
                    var trimmed = query.TrimStart('?');
                    foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var idx = part.IndexOf('=');
                        if (idx <= 0)
                        {
                            continue;
                        }

                        var key = part.Substring(0, idx);
                        var val = Uri.UnescapeDataString(part.Substring(idx + 1));
                        if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
                        {
                            relativeFilePath = val.Replace("\\", "/");
                        }
                        else if (key.Equals("m", StringComparison.OrdinalIgnoreCase))
                        {
                            mValue = val;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(relativeFilePath))
                {
                    return originalUrl;
                }

                var publishedBase = BuildPublishedFileUrl(ctx, relativeFilePath);
                if (!string.IsNullOrWhiteSpace(mValue))
                {
                    publishedBase = AppendQueryParamIfMissing(publishedBase, "m", mValue);
                }

                return publishedBase;
            }
            catch
            {
                return originalUrl;
            }
        }

        var updated = mdPattern.Replace(markdownContent, m =>
        {
            var rewritten = Rewriter(m.Groups["url"].Value);
            return m.Groups["head"].Value + rewritten + m.Groups["tail"].Value;
        });

        updated = htmlDqPattern.Replace(updated, m =>
        {
            var url = m.Groups["url"].Value;
            return m.Value.Replace(url, Rewriter(url));
        });

        updated = htmlSqPattern.Replace(updated, m =>
        {
            var url = m.Groups["url"].Value;
            return m.Value.Replace(url, Rewriter(url));
        });

        return updated;
    }

    private static void ExtractFilenamePathsFromJsonArray(
        JsonElement root,
        string propertyName,
        Dictionary<string, string> map)
    {
        if (!root.TryGetProperty(propertyName, out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var filePath = fileElement.GetString();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            var normalizedPath = filePath.Replace("\\", "/");
            var filename = Path.GetFileName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(filename) && !map.ContainsKey(filename))
            {
                map[filename] = normalizedPath;
            }
        }
    }

    private static string ApplyFilenameUrlReplacements(
        string content,
        IDictionary<string, string> filenameUrlMap,
        string messageStamp)
    {
        var result = content;
        foreach (var kv in filenameUrlMap)
        {
            var filename = kv.Key;
            var canonicalUrlWithStamp = AppendQueryParamIfMissing(kv.Value, "m", messageStamp);

            var mdPattern = new Regex(
                @"(?<head>(!\[[^\]]*\]|\[[^\]]*\])\()(?<url>[^)]+)(?<tail>\))",
                RegexOptions.Compiled);
            result = mdPattern.Replace(result, m =>
            {
                var url = m.Groups["url"].Value;
                return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0
                       && !url.Equals(canonicalUrlWithStamp, StringComparison.OrdinalIgnoreCase)
                    ? m.Groups["head"].Value + canonicalUrlWithStamp + m.Groups["tail"].Value
                    : m.Value;
            });

            var htmlAttrPattern = new Regex(
                @"(?<attr>href|src)\s*=\s*""(?<url>[^""]+)""",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            result = htmlAttrPattern.Replace(result, m =>
            {
                var url = m.Groups["url"].Value;
                return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0
                       && !url.Equals(canonicalUrlWithStamp, StringComparison.OrdinalIgnoreCase)
                    ? m.Value.Replace(url, canonicalUrlWithStamp)
                    : m.Value;
            });

            var htmlAttrPatternSingle = new Regex(
                @"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            result = htmlAttrPatternSingle.Replace(result, m =>
            {
                var url = m.Groups["url"].Value;
                return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0
                       && !url.Equals(canonicalUrlWithStamp, StringComparison.OrdinalIgnoreCase)
                    ? m.Value.Replace(url, canonicalUrlWithStamp)
                    : m.Value;
            });
        }

        return result;
    }
}
