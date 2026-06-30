using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

public interface IMcpSandboxSetupStagingService
{
    /// <summary>
    /// Writes scoped sandbox admin state for all sandbox_subprocess MCP sources on a guide (E12 — stage only, no apply).
    /// </summary>
    Task StageGuideSandboxSetupAsync(
        Guid projectId,
        Guid guideId,
        IReadOnlyList<CustomToolDto>? customTools,
        CancellationToken cancellationToken = default);
}
