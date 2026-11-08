using System.Text.Json;

namespace ScriptExecutionAgent;

/// <summary>
/// JSON options preserved from the original agent wire contract. Agent responses were
/// serialized with BCL defaults (PascalCase) on /execute and /files, and with Web defaults
/// (camelCase) on the MCP and admin routes; Results.Json honours these explicitly.
/// </summary>
internal static class AgentJson
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.General);

    /// <summary>MCP routes: camelCase only, so null properties are still written.</summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Admin routes: Web defaults (camelCase, nulls omitted), as staged originally.</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static readonly JsonSerializerOptions RequestRead = new() { PropertyNameCaseInsensitive = true };
}
