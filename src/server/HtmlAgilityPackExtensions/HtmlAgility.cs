using HtmlAgilityPack;
using System.Security.Cryptography;
using System.Text;

namespace HtmlAgility
{
    /// <summary>
    /// Result of converting HTML to Markdown, including extracted links and images.
    /// </summary>
    public class MarkdownConversionResult
    {
        /// <summary>
        /// The markdown content extracted from the HTML.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Dictionary of page links where key is the link text and value is the absolute URL.
        /// If multiple links have the same text, a numeric suffix is added to make keys unique.
        /// </summary>
        public Dictionary<string, string> PageLinks { get; set; } = new();

        /// <summary>
        /// Dictionary of image links where key is the alt text (or a hash of the URL if no alt text)
        /// and value is the absolute URL.
        /// </summary>
        public Dictionary<string, string> ImageLinks { get; set; } = new();
    }

    /// <summary>
    /// Provides extension methods for the HtmlDocument class.
    /// </summary>
    public static class HtmlAgilityPackExtensions
    {
        static HttpClient _httpClient = new();

        static HtmlAgilityPackExtensions()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("None");
            _httpClient.DefaultRequestHeaders.Connection.ParseAdd("keep-alive");
        }

        /// <summary>
        /// Converts an HtmlDocument to its Markdown representation.
        /// </summary>
        /// <param name="htmlDocument">The HtmlDocument to convert.</param>
        /// <returns>A MarkdownConversionResult containing the content, page links, and image links.</returns>
        public static MarkdownConversionResult ConvertToMarkdown(this HtmlDocument htmlDocument)
        {
            return ConvertToMarkdown(htmlDocument, null, 100);
        }

        /// <summary>
        /// Converts an HtmlDocument to its Markdown representation with a base URI for resolving relative URLs.
        /// </summary>
        /// <param name="htmlDocument">The HtmlDocument to convert.</param>
        /// <param name="baseUri">Base URI for resolving relative URLs in links and images.</param>
        /// <returns>A MarkdownConversionResult containing the content, page links, and image links.</returns>
        public static MarkdownConversionResult ConvertToMarkdown(this HtmlDocument htmlDocument, Uri? baseUri)
        {
            return ConvertToMarkdown(htmlDocument, baseUri, 100);
        }

        /// <summary>
        /// Converts an HtmlDocument to its Markdown representation with a custom maximum depth.
        /// </summary>
        /// <param name="htmlDocument">The HtmlDocument to convert.</param>
        /// <param name="maxDepth">Maximum recursion depth to prevent stack overflow (default: 100).</param>
        /// <returns>A MarkdownConversionResult containing the content, page links, and image links.</returns>
        public static MarkdownConversionResult ConvertToMarkdown(this HtmlDocument htmlDocument, int maxDepth)
        {
            return ConvertToMarkdown(htmlDocument, null, maxDepth);
        }

        /// <summary>
        /// Converts an HtmlDocument to its Markdown representation with a base URI and custom maximum depth.
        /// </summary>
        /// <param name="htmlDocument">The HtmlDocument to convert.</param>
        /// <param name="baseUri">Base URI for resolving relative URLs in links and images.</param>
        /// <param name="maxDepth">Maximum recursion depth to prevent stack overflow (default: 100).</param>
        /// <returns>A MarkdownConversionResult containing the content, page links, and image links.</returns>
        public static MarkdownConversionResult ConvertToMarkdown(this HtmlDocument htmlDocument, Uri? baseUri, int maxDepth)
        {
            var result = new MarkdownConversionResult();

            if (htmlDocument?.DocumentNode == null)
            {
                return result;
            }

            if (maxDepth <= 0)
            {
                maxDepth = 50; // Minimum safe depth
            }

            StringBuilder markdownBuilder = new StringBuilder();
            var visitedNodes = new HashSet<HtmlNode>();

            try
            {
                ConvertNodeToMarkdown(htmlDocument.DocumentNode, markdownBuilder, visitedNodes, 0, maxDepth, baseUri, result.PageLinks, result.ImageLinks);
                result.Content = markdownBuilder.ToString();
            }
            catch (Exception ex)
            {
                result.Content = $"Error during conversion: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Recursively converts an HtmlNode to its Markdown representation with safety protections.
        /// </summary>
        /// <param name="node">The HtmlNode to convert.</param>
        /// <param name="markdownBuilder">The StringBuilder to append the Markdown representation to.</param>
        /// <param name="visitedNodes">Set of already visited nodes to prevent circular references.</param>
        /// <param name="depth">Current recursion depth.</param>
        /// <param name="maxDepth">Maximum allowed recursion depth.</param>
        /// <param name="baseUri">Base URI for resolving relative URLs.</param>
        /// <param name="pageLinks">Dictionary to collect page links (link text -> URL).</param>
        /// <param name="imageLinks">Dictionary to collect image links (alt text or hash -> URL).</param>
        private static void ConvertNodeToMarkdown(HtmlNode node, StringBuilder markdownBuilder, HashSet<HtmlNode> visitedNodes, int depth, int maxDepth, Uri? baseUri, Dictionary<string, string>? pageLinks, Dictionary<string, string>? imageLinks)
        {
            // Safety checks
            if (node == null || markdownBuilder == null || depth >= maxDepth)
            {
                return;
            }

            // Prevent circular references
            if (visitedNodes.Contains(node))
            {
                return;
            }

            // Add current node to visited set
            visitedNodes.Add(node);

            try
            {
                bool ignoreChildren = false;
                string nodeName = node.Name?.ToLower() ?? string.Empty;
                
                switch (nodeName)
                {
                    case "h1":
                        markdownBuilder.AppendLine($"# {CleanText(node.InnerText)}");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true; // Process as text only for headers
                        break;
                    case "h2":
                        markdownBuilder.AppendLine($"## {CleanText(node.InnerText)}");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "h3":
                        markdownBuilder.AppendLine($"### {CleanText(node.InnerText)}");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "h4":
                        markdownBuilder.AppendLine($"#### {CleanText(node.InnerText)}");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "h5":
                        markdownBuilder.AppendLine($"##### {CleanText(node.InnerText)}");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "h6":
                        markdownBuilder.AppendLine($"###### {CleanText(node.InnerText)}");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "p":
                        if (!string.IsNullOrWhiteSpace(node.InnerText))
                        {
                            markdownBuilder.AppendLine(CleanText(node.InnerText));
                            markdownBuilder.AppendLine();
                        }
                        ignoreChildren = true;
                        break;
                    case "a":
                        string href = node.GetAttributeValue("href", "#");
                        string linkText = CleanText(node.InnerText);
                        if (!string.IsNullOrWhiteSpace(linkText))
                        {
                            href = ResolveUrl(href, baseUri);
                            markdownBuilder.Append($"[{linkText}]({href})");

                            // Collect page link if it's a navigable URL
                            if (pageLinks != null && IsNavigableUrl(href))
                            {
                                AddToDictionaryWithUniqueKey(pageLinks, linkText, href);
                            }
                        }
                        ignoreChildren = true;
                        break;
                    case "img":
                        string src = node.GetAttributeValue("src", "");
                        string alt = node.GetAttributeValue("alt", "");
                        // Skip base64 data URIs - they bloat the markdown and aren't useful as text
                        if (!string.IsNullOrEmpty(src) && !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            src = ResolveUrl(src, baseUri);
                            markdownBuilder.AppendLine($"![{alt}]({src})");

                            // Collect image link
                            if (imageLinks != null)
                            {
                                // Use alt text as key, or generate a hash from URL if no alt text
                                string imageKey = !string.IsNullOrWhiteSpace(alt)
                                    ? alt
                                    : GenerateShortHash(src);
                                AddToDictionaryWithUniqueKey(imageLinks, imageKey, src);
                            }
                        }
                        else if (!string.IsNullOrEmpty(alt))
                        {
                            // If there's alt text, include it as a description
                            markdownBuilder.AppendLine($"[Image: {alt}]");
                        }
                        // Otherwise skip the image entirely
                        ignoreChildren = true;
                        break;
                    case "ul":
                        ProcessList(node, markdownBuilder, visitedNodes, depth, maxDepth, false);
                        ignoreChildren = true;
                        break;
                    case "ol":
                        ProcessList(node, markdownBuilder, visitedNodes, depth, maxDepth, true);
                        ignoreChildren = true;
                        break;
                    case "li":
                        // List items are handled by their parent ul/ol
                        break;
                    case "b":
                    case "strong":
                        markdownBuilder.Append($"**{CleanText(node.InnerText)}**");
                        ignoreChildren = true;
                        break;
                    case "i":
                    case "em":
                        markdownBuilder.Append($"*{CleanText(node.InnerText)}*");
                        ignoreChildren = true;
                        break;
                    case "code":
                        markdownBuilder.Append($"`{CleanText(node.InnerText)}`");
                        ignoreChildren = true;
                        break;
                    case "pre":
                        markdownBuilder.AppendLine();
                        markdownBuilder.AppendLine("```");
                        markdownBuilder.AppendLine(CleanText(node.InnerText));
                        markdownBuilder.AppendLine("```");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "blockquote":
                        var lines = CleanText(node.InnerText).Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            markdownBuilder.AppendLine($"> {line.Trim()}");
                        }
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "br":
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "hr":
                        markdownBuilder.AppendLine();
                        markdownBuilder.AppendLine("---");
                        markdownBuilder.AppendLine();
                        ignoreChildren = true;
                        break;
                    case "script":
                    case "style":
                    case "#comment":
                    case "noscript":
                    case "meta":
                    case "link":
                    case "head":
                    case "title":
                        ignoreChildren = true;
                        break;
                    case "#text":
                        string textContent = CleanText(node.InnerText);
                        if (!string.IsNullOrWhiteSpace(textContent))
                        {
                            markdownBuilder.Append(textContent);
                        }
                        break;
                    default:
                        // For unknown elements, process children but don't add special formatting
                        break;
                }

                // Process child nodes if not ignored and within depth limits
                if (node.HasChildNodes && !ignoreChildren && depth < maxDepth - 1)
                {
                    foreach (var child in node.ChildNodes)
                    {
                        if (child != null)
                        {
                            ConvertNodeToMarkdown(child, markdownBuilder, visitedNodes, depth + 1, maxDepth, baseUri, pageLinks, imageLinks);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the exception if needed, but continue processing
                markdownBuilder.AppendLine($"[Error processing node: {ex.Message}]");
            }
            finally
            {
                // Remove from visited set to allow processing in different contexts
                visitedNodes.Remove(node);
            }
        }

        /// <summary>
        /// Processes list elements (ul/ol) safely.
        /// </summary>
        /// <param name="listNode">The list node (ul or ol).</param>
        /// <param name="markdownBuilder">The StringBuilder to append to.</param>
        /// <param name="visitedNodes">Set of visited nodes.</param>
        /// <param name="depth">Current depth.</param>
        /// <param name="maxDepth">Maximum depth.</param>
        /// <param name="isOrdered">Whether this is an ordered list.</param>
        private static void ProcessList(HtmlNode listNode, StringBuilder markdownBuilder, HashSet<HtmlNode> visitedNodes, int depth, int maxDepth, bool isOrdered)
        {
            if (listNode == null || depth >= maxDepth) return;

            var listItems = listNode.SelectNodes(".//li");
            if (listItems != null && listItems.Count > 0)
            {
                int index = 1;
                foreach (var li in listItems)
                {
                    if (li != null && !visitedNodes.Contains(li))
                    {
                        string itemText = CleanText(li.InnerText);
                        if (!string.IsNullOrWhiteSpace(itemText))
                        {
                            if (isOrdered)
                            {
                                markdownBuilder.AppendLine($"{index}. {itemText}");
                                index++;
                            }
                            else
                            {
                                markdownBuilder.AppendLine($"* {itemText}");
                            }
                        }
                    }
                }
                markdownBuilder.AppendLine();
            }
        }

        /// <summary>
        /// Cleans the text by removing excessive whitespace and tabs.
        /// </summary>
        /// <param name="text">The text to clean.</param>
        /// <returns>The cleaned text.</returns>
        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            try
            {
                // Replace tabs with a single space
                text = text.Replace("\t", " ");

                // Replace multiple whitespace characters with a single space
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

                // Remove HTML entities
                text = System.Net.WebUtility.HtmlDecode(text);

                // Trim leading and trailing whitespace
                return text.Trim();
            }
            catch (Exception)
            {
                // If text cleaning fails, return the original text trimmed
                return text?.Trim() ?? string.Empty;
            }
        }

        /// <summary>
        /// Resolves a URL against a base URI, converting relative URLs to absolute.
        /// </summary>
        /// <param name="url">The URL to resolve (may be relative or absolute).</param>
        /// <param name="baseUri">The base URI to resolve against.</param>
        /// <returns>The resolved absolute URL, or the original URL if resolution fails or is not needed.</returns>
        private static string ResolveUrl(string url, Uri? baseUri)
        {
            if (string.IsNullOrEmpty(url) || baseUri == null)
            {
                return url;
            }

            // Skip resolution for certain URL types that should remain as-is
            if (url.StartsWith("#", StringComparison.Ordinal) ||           // Anchor links
                url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            try
            {
                // Uri constructor handles both relative and absolute URLs correctly
                // If url is already absolute, it returns url unchanged
                // If url is relative, it resolves it against baseUri
                var resolvedUri = new Uri(baseUri, url);
                return resolvedUri.ToString();
            }
            catch
            {
                // If resolution fails (malformed URL), return the original
                return url;
            }
        }

        /// <summary>
        /// Determines if a URL is navigable (HTTP/HTTPS page link, not an anchor, javascript, etc.).
        /// </summary>
        /// <param name="url">The URL to check.</param>
        /// <returns>True if the URL is a navigable page link.</returns>
        private static bool IsNavigableUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            // Exclude non-navigable URL types
            if (url.StartsWith("#", StringComparison.Ordinal) ||
                url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Must be HTTP or HTTPS
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds a key-value pair to a dictionary, ensuring the key is unique by appending a numeric suffix if needed.
        /// </summary>
        /// <param name="dictionary">The dictionary to add to.</param>
        /// <param name="key">The desired key.</param>
        /// <param name="value">The value to add.</param>
        private static void AddToDictionaryWithUniqueKey(Dictionary<string, string> dictionary, string key, string value)
        {
            if (dictionary.ContainsKey(key))
            {
                // If the same key exists with the same value, skip (duplicate link)
                if (dictionary[key] == value)
                {
                    return;
                }

                // Find a unique key by appending a number
                int suffix = 2;
                string uniqueKey;
                do
                {
                    uniqueKey = $"{key} ({suffix})";
                    suffix++;
                } while (dictionary.ContainsKey(uniqueKey));

                dictionary[uniqueKey] = value;
            }
            else
            {
                dictionary[key] = value;
            }
        }

        /// <summary>
        /// Generates a short hash from a string (used for image URLs without alt text).
        /// </summary>
        /// <param name="input">The input string to hash.</param>
        /// <returns>A short hash string (8 characters).</returns>
        private static string GenerateShortHash(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA256.HashData(bytes);
            // Take first 4 bytes and convert to hex (8 characters)
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        /// <summary>
        /// Reads the HTML content from the specified URL and converts it to its Markdown representation.
        /// </summary>
        /// <param name="url">The URL of the HTML page to convert.</param>
        /// <returns>A MarkdownConversionResult containing the content, page links, and image links.</returns>
        public static async Task<MarkdownConversionResult> ConvertUrlToMarkdownAsync(string url)
        {
            var errorResult = new MarkdownConversionResult();

            if (string.IsNullOrWhiteSpace(url))
            {
                errorResult.Content = "Error: URL cannot be null or empty.";
                return errorResult;
            }

            // Validate URL format and create base URI for resolving relative URLs
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            {
                errorResult.Content = "Error: Invalid URL format. Only HTTP and HTTPS URLs are supported.";
                return errorResult;
            }

            string? htmlContent;
            int timeoutInSeconds = 15; // Increased timeout for better reliability

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutInSeconds)))
            {
                try
                {
                    htmlContent = await _httpClient.GetStringAsync(url, cts.Token);

                    if (string.IsNullOrWhiteSpace(htmlContent))
                    {
                        errorResult.Content = "Error: Retrieved content is empty.";
                        return errorResult;
                    }

                    // Check content size to prevent processing extremely large documents
                    if (htmlContent.Length > 10_000_000) // 10MB limit
                    {
                        errorResult.Content = "Error: Content too large to process safely.";
                        return errorResult;
                    }
                }
                catch (TaskCanceledException)
                {
                    errorResult.Content = "Error: Request timed out.";
                    return errorResult;
                }
                catch (HttpRequestException ex)
                {
                    errorResult.Content = $"Error: HTTP request failed - {ex.Message}";
                    return errorResult;
                }
                catch (Exception ex)
                {
                    errorResult.Content = $"Error: {ex.Message}";
                    return errorResult;
                }
            }

            try
            {
                var htmlDocument = new HtmlDocument();
                htmlDocument.LoadHtml(htmlContent);

                // Add timeout for conversion process
                using (var conversionCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var conversionTask = Task.Run(() => htmlDocument.ConvertToMarkdown(baseUri), conversionCts.Token);
                    var result = await conversionTask;
                    return result;
                }
            }
            catch (TaskCanceledException)
            {
                errorResult.Content = "Error: Markdown conversion timed out. Content may be too complex.";
                return errorResult;
            }
            catch (StackOverflowException)
            {
                errorResult.Content = "Error: Content structure is too complex to process safely.";
                return errorResult;
            }
            catch (OutOfMemoryException)
            {
                errorResult.Content = "Error: Content is too large and caused memory issues.";
                return errorResult;
            }
            catch (Exception ex)
            {
                // Fallback: return cleaned HTML content if markdown conversion fails
                try
                {
                    var fallbackDoc = new HtmlDocument();
                    fallbackDoc.LoadHtml(htmlContent);
                    var textContent = fallbackDoc.DocumentNode?.InnerText;
                    if (!string.IsNullOrWhiteSpace(textContent))
                    {
                        errorResult.Content = $"Warning: Markdown conversion failed, returning text content:\n\n{CleanText(textContent)}";
                        return errorResult;
                    }
                }
                catch
                {
                    // Ignore fallback errors
                }

                errorResult.Content = $"Error: Markdown conversion failed - {ex.Message}";
                return errorResult;
            }
        }

        /// <summary>
        /// Reads the HTML content from the specified URL and converts it to its Markdown representation.
        /// Note: This is a synchronous wrapper around the async method. Consider using ConvertUrlToMarkdownAsync for better performance.
        /// </summary>
        /// <param name="url">The URL of the HTML page to convert.</param>
        /// <returns>A MarkdownConversionResult containing the content, page links, and image links.</returns>
        public static MarkdownConversionResult ConvertUrlToMarkdown(string url)
        {
            var errorResult = new MarkdownConversionResult();

            try
            {
                // Use the async version with proper timeout handling
                var task = ConvertUrlToMarkdownAsync(url);
                task.Wait(TimeSpan.FromSeconds(45)); // Overall timeout for sync version

                if (task.IsCompletedSuccessfully)
                {
                    return task.Result;
                }
                else if (task.IsCanceled)
                {
                    errorResult.Content = "Error: Operation was cancelled or timed out.";
                    return errorResult;
                }
                else if (task.IsFaulted)
                {
                    errorResult.Content = $"Error: {task.Exception?.GetBaseException().Message ?? "Unknown error occurred."}";
                    return errorResult;
                }
                else
                {
                    errorResult.Content = "Error: Operation did not complete within the expected time.";
                    return errorResult;
                }
            }
            catch (AggregateException ex)
            {
                var innerException = ex.GetBaseException();
                errorResult.Content = $"Error: {innerException.Message}";
                return errorResult;
            }
            catch (Exception ex)
            {
                errorResult.Content = $"Error: {ex.Message}";
                return errorResult;
            }
        }
    }
}