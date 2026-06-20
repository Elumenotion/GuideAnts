using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

public interface IMcpToolSourceDiscoveryService
{
    Task<McpTestConnectionResponse> TestConnectionAsync(
        McpTestConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task<McpDiscoverToolsResponse> DiscoverToolsAsync(
        McpDiscoverToolsRequest request,
        CancellationToken cancellationToken = default);
}
