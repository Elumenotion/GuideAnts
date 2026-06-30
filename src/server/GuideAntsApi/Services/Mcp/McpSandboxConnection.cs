using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Resolved MCP sandbox subprocess connection metadata from an OpenAPI descriptor.
/// </summary>
public sealed record McpSandboxConnection(
    string BridgeId,
    McpPackageDescriptorDto Package,
    IReadOnlyList<McpEnvironmentVariableRefDto> EnvironmentVariableRefs,
    string? ToolNamePrefix);
