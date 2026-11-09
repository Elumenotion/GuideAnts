using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScriptExecutionAgent;

/// <summary>
/// Maps unhandled exceptions to the plain-text error responses the agent wrote before
/// handlers moved out of Program.cs. Log messages and status codes are unchanged.
/// </summary>
internal static class AgentErrorResult
{
    public static IResult FromExecutionRoute(string route, Exception exception, ILogger logger)
    {
        if (exception is RequestBodyTooLargeException)
        {
            return FromRequestBodyTooLarge(exception, route, logger);
        }

        if (exception is JsonException jsonEx)
        {
            logger.LogError(jsonEx, $"{route} JSON parsing exception");
            return AgentResponses.Text($"JSON parsing error: {jsonEx.Message}", StatusCodes.Status400BadRequest);
        }

        logger.LogError(exception, $"{route} unexpected exception");
        return AgentResponses.Text($"Internal server error: {exception.Message}", StatusCodes.Status500InternalServerError);
    }

    public static IResult FromFileListing(Exception exception, ILogger logger)
    {
        logger.LogError(exception, "Error listing files");
        return AgentResponses.Text($"Error: {exception.Message}", StatusCodes.Status500InternalServerError);
    }

    public static IResult FromRequestBodyTooLarge(Exception exception, string route, ILogger logger)
    {
        var tooLarge = (RequestBodyTooLargeException)exception;
        logger.LogWarning("SECURITY: {Route} rejected because request body exceeds {MaxBytes} bytes.", route, tooLarge.MaxBytes);
        return AgentResponses.Text("Request body too large", StatusCodes.Status413RequestEntityTooLarge);
    }
}
