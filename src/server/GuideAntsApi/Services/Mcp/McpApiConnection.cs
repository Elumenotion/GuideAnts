namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Resolved MCP API connection metadata from an OpenAPI descriptor.
/// </summary>
public sealed record McpApiConnection(
    string BridgeId,
    string Url,
    IReadOnlyDictionary<string, string> HeaderTemplates,
    string? ToolNamePrefix);
