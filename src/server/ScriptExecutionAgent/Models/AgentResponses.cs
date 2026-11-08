using System.Text;

namespace ScriptExecutionAgent;

/// <summary>
/// Plain-text responses for the agent HTTP surface. The agent answers validation and error
/// cases as text/plain (not problem+json); callers read the message verbatim.
/// </summary>
internal static class AgentResponses
{
    public static IResult Text(string message, int statusCode) =>
        Results.Text(message, "text/plain", Encoding.UTF8, statusCode);

    public static IResult Plain(string content) =>
        Results.Text(content, "text/plain", Encoding.UTF8);
}
