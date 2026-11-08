using System.Text;
using System.Text.Json;

namespace ScriptExecutionAgent;

/// <summary>
/// Reads request bodies with the agent's original tolerant settings. Bodies are deserialized
/// manually (not bound as handler parameters) so malformed JSON keeps producing the plain-text
/// 400 messages the agent has always returned instead of an ASP.NET problem+json response.
/// </summary>
internal static class AgentRequestBody
{
    public static async Task<T?> ReadJsonAsync<T>(HttpContext context) where T : class
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        return string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<T>(body, AgentJson.RequestRead);
    }

    public static async Task<string> ReadTextAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(context.RequestAborted);
    }
}
