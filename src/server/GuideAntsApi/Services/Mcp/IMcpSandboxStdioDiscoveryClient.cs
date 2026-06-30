namespace GuideAntsApi.Services.Mcp;

public interface IMcpSandboxStdioDiscoveryClient
{
    Task<McpStdioDiscoverResponse> DiscoverAsync(
        Guid projectId,
        Guid guideId,
        Models.Guides.McpToolSourceConnectionDto connection,
        CancellationToken cancellationToken = default);
}
