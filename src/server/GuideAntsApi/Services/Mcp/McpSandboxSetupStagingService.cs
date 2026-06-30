using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

public sealed class McpSandboxSetupStagingService(
    McpSandboxAdminApiClient adminClient,
    ILogger<McpSandboxSetupStagingService> logger) : IMcpSandboxSetupStagingService
{
    public async Task StageGuideSandboxSetupAsync(
        Guid projectId,
        Guid guideId,
        IReadOnlyList<CustomToolDto>? customTools,
        CancellationToken cancellationToken = default)
    {
        if (customTools is null || customTools.Count == 0)
        {
            return;
        }

        var specs = customTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.OpenApiSpec))
            .Select(tool => tool.OpenApiSpec)
            .ToList();

        if (!McpSandboxSetupComposer.TryCollectSandboxPackages(specs, out var packages))
        {
            return;
        }

        var artifacts = McpSandboxSetupComposer.Compose(packages);
        var scopeQuery = McpSandboxAdminApiClient.BuildScopedQuery(projectId, guideId);

        var requirementsOk = await adminClient.PutTextAsync(
            "requirements",
            scopeQuery,
            artifacts.RequirementsText,
            "text/plain",
            cancellationToken);
        if (!requirementsOk)
        {
            logger.LogWarning(
                "Failed to stage scoped requirements for guide {GuideId} in project {ProjectId}",
                guideId,
                projectId);
        }

        var installScriptsOk = await adminClient.PutTextAsync(
            "install-scripts",
            scopeQuery,
            artifacts.InstallScriptsJson,
            "application/json",
            cancellationToken);
        if (!installScriptsOk)
        {
            logger.LogWarning(
                "Failed to stage scoped install scripts for guide {GuideId} in project {ProjectId}",
                guideId,
                projectId);
        }

        if (!string.IsNullOrWhiteSpace(artifacts.AptPackagesText))
        {
            var existingApt = await adminClient.GetTextAsync("apt-packages", query: null, cancellationToken) ?? string.Empty;
            var mergedApt = MergeAptPackages(existingApt, artifacts.AptPackagesText);
            var aptOk = await adminClient.PutTextAsync(
                "apt-packages",
                query: null,
                mergedApt,
                "text/plain",
                cancellationToken);
            if (!aptOk)
            {
                logger.LogWarning(
                    "Failed to stage global apt packages while staging guide {GuideId}",
                    guideId);
            }
        }

        logger.LogInformation(
            "Staged sandbox MCP setup for guide {GuideId} ({PackageCount} packages)",
            guideId,
            packages.Count);
    }

    private static string MergeAptPackages(string existing, string additions)
    {
        var lines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (existing + '\n' + additions).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            {
                lines.Add(line.Trim());
            }
        }

        return lines.Count == 0 ? string.Empty : string.Join('\n', lines.OrderBy(static line => line, StringComparer.OrdinalIgnoreCase)) + '\n';
    }
}
