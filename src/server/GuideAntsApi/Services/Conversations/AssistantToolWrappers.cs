using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;
using AntRunner.ToolCalling.Functions;
using System.Text.RegularExpressions;

namespace AntRunner.Chat;

public static class AssistantToolWrappers
{
    private static readonly Regex HttpUrlRegex = new(
        @"https?://[^\s<>()""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads a web page and returns markdown of the content.
    /// </summary>
    [Tool(
        OperationId = "ReadWeb",
        Summary = "Reads a web page and returns markdown of the content"
    )]
    [RequiresNotebookContext]
    public static async Task<ScriptExecutionResult> ReadWeb(
        [Parameter(Description = "Required. A url to a page and a question or statement that the assitant will use to extract relevant content. Example 'what is the timeout of the foo operation? http://www.example.com/doc'")]
        string instructions,
        
        [Parameter(Description = "Invocation context", Hidden = true)]
        InvocationContext? context = null)
    {
        if (!ContainsAbsoluteHttpUrl(instructions))
        {
            const string errorMessage =
                "ERROR: Missing URL for ReadWeb. Provide an absolute HTTP/HTTPS URL in `instructions` and try again. " +
                "Example: \"Summarize this article: https://example.com/article\".";

            return new ScriptExecutionResult
            {
                StandardOutput = errorMessage,
                StandardError = errorMessage
            };
        }

        var result = await Agent.Invoke("Read Web", instructions, context!);
        return result;
    }

    private static bool ContainsAbsoluteHttpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in HttpUrlRegex.Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
