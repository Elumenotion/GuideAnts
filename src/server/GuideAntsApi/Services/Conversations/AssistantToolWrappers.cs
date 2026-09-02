using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;
using AntRunner.ToolCalling.Functions;

namespace AntRunner.Chat;

public static class AssistantToolWrappers
{
    /// <summary>
    /// Reads a web page and returns markdown of the content.
    /// </summary>
    [Tool(
        OperationId = "ReadWeb",
        Summary = "Reads a web page and returns markdown of the content"
    )]
    [RequiresNotebookContext]
    public static async Task<ScriptExecutionResult> ReadWeb(
        [Parameter(Description = "Required. Absolute HTTP or HTTPS URL of the page to read.", Required = true)]
        string url,

        [Parameter(Description = "Required. Question or statement describing what content to extract from the page.", Required = true)]
        string instructions,

        [Parameter(Description = "Invocation context", Hidden = true)]
        InvocationContext? context = null,

        CancellationToken cancellationToken = default)
    {
        if (!TryCreateHttpUri(url, out _))
        {
            const string errorMessage =
                "ERROR: Invalid URL for ReadWeb. Provide an absolute HTTP/HTTPS URL in `url` and try again. " +
                "Example: \"https://example.com/article\".";

            return new ScriptExecutionResult
            {
                StandardOutput = errorMessage,
                StandardError = errorMessage
            };
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            const string errorMessage =
                "ERROR: Missing instructions for ReadWeb. Provide a question or statement in `instructions` describing what to extract from the page.";

            return new ScriptExecutionResult
            {
                StandardOutput = errorMessage,
                StandardError = errorMessage
            };
        }

        var prompt = $"{instructions.Trim()}\n\n{url.Trim()}";
        var result = await Agent.Invoke("Read Web", prompt, context!, cancellationToken);
        return result;
    }

    private static bool TryCreateHttpUri(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null!;
        return false;
    }
}
