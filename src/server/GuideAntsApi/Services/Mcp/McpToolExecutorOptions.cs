namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Per-call MCP HTTP execution settings (design E5).
/// </summary>
public sealed class McpToolExecutorOptions
{
    public const string SectionName = "McpToolExecution";

    /// <summary>
    /// Per-call timeout for MCP tools/call (seconds). No connection pooling in v1.
    /// </summary>
    public int CallTimeoutSeconds { get; set; } = 30;
}
