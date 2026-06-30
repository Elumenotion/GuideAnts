using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

public interface IMcpSandboxPublishGateService
{
    /// <summary>
    /// Returns a publish-blocking error when any sandbox_subprocess MCP source has staged ≠ applied admin state (E16).
    /// </summary>
    Task<string?> GetPublishBlockMessageAsync(
        Guid projectId,
        Guid guideId,
        IReadOnlyList<CustomToolDto>? customTools,
        CancellationToken cancellationToken = default);
}
